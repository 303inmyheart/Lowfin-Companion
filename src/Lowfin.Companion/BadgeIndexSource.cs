using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Configuration;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Lowfin.Companion;

public sealed class BadgeIndexSource(ILibraryManager library,IUserManager users,CacheFeed cache,IServerConfigurationManager? configuration=null) : IBadgeIndexSource {
    public static bool Supported(BaseItem item)=>item is Video or Series or Season;
    public static string? Revision(BaseItem item)=>item.DateLastSaved==default?null:$"facts-v1:{item.DateLastSaved.Ticks}:{item.DateModified.Ticks}:{item.ParentId:N}:{(item is Episode episode?episode.SeriesId:Guid.Empty):N}:{(item is Episode ep?ep.SeasonId:Guid.Empty):N}:{item.Width}:{item.Height}";
    private static BadgeIndexItem Entry(BaseItem item,BadgeIndexItem? cached=null) {
        string? revision=Revision(item);
        // DateLastSaved is also Jellyfin's native ETag input. Events still force a fresh read.
        if(revision!=null&&cached?.Revision==revision&&cached.Labels!=null) return cached;
        Guid[] parents=item switch {
            Episode episode=>new[]{episode.SeriesId,episode.ParentId,episode.SeasonId}.Where(x=>x!=Guid.Empty).Distinct().ToArray(),
            Season season=>new[]{season.ParentId}.Where(x=>x!=Guid.Empty).ToArray(),
            _=>[]
        };
        var video=item as Video;var streams=video?.GetMediaSources(false).FirstOrDefault()?.MediaStreams??[];
        string access=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new {item.OfficialRating,item.CustomRating,item.PreferredMetadataCountryCode,item.InheritedParentalRatingValue,item.InheritedParentalRatingSubValue,item.SourceType,tags=item.Tags.Select(x=>x.ToLowerInvariant()).Distinct().Order().ToArray()})));
        return new(item.Id,video==null?0:MediaBadges.Detect(streams),item is Series or Season,parents,video==null?[]:ExpandedBadges.Detect(streams,video.Video3DFormat.HasValue),revision,access);
    }
    public int? TotalCount()=>library.GetItemsResult(new InternalItemsQuery { Recursive=true,IncludeItemTypes=[BaseItemKind.Movie,BaseItemKind.Video,BaseItemKind.Episode,BaseItemKind.Series,BaseItemKind.Season],IsVirtualItem=false,Limit=0,EnableTotalRecordCount=true }).TotalRecordCount;
    public IEnumerable<BadgeIndexItem> All(CancellationToken cancellation)=>All(new Dictionary<Guid,BadgeIndexItem>(),cancellation);
    public IEnumerable<BadgeIndexItem> All(IReadOnlyDictionary<Guid,BadgeIndexItem> previous,CancellationToken cancellation) {
        for(int offset=0;;offset+=200) {
            cancellation.ThrowIfCancellationRequested();
            var page=library.GetItemList(new InternalItemsQuery { Recursive=true,IncludeItemTypes=[BaseItemKind.Movie,BaseItemKind.Video,BaseItemKind.Episode,BaseItemKind.Series,BaseItemKind.Season],IsVirtualItem=false,StartIndex=offset,Limit=200,EnableTotalRecordCount=false });
            foreach(var item in page) { cancellation.ThrowIfCancellationRequested();yield return Entry(item,previous.GetValueOrDefault(item.Id)); }
            if(page.Count<200) yield break;
        }
    }
    public BadgeIndexItem? Read(Guid id) { var item=library.GetItemById(id);return item!=null&&Supported(item)&&!item.IsVirtualItem?Entry(item):null; }
    public IReadOnlyList<BadgeIndexUser> Users()=>users.GetUsers().Select(user=>new BadgeIndexUser(user.Id,cache.Scope(user))).ToArray();
    public string? Visibility(Guid userId,CancellationToken cancellation) {
        var user=users.GetUserById(userId);if(user==null) return "missing";
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes("visibility-v1:"+(configuration?.Configuration.MetadataCountryCode??"")));
        // ID-only native queries detect permission/filter/membership changes without loading streams
        // or issuing one descendant query per season. Never infer access from the shared facts index.
        foreach(bool episodes in new[]{true,false}) {
            cancellation.ThrowIfCancellationRequested();
            var query=new InternalItemsQuery(user) { Recursive=true,IncludeItemTypes=episodes?[BaseItemKind.Episode]:[BaseItemKind.Series,BaseItemKind.Season],IsVirtualItem=false,EnableTotalRecordCount=false };
            if(episodes) query.IsMissing=false;
            foreach(var id in library.GetItemIds(query).Order()) {cancellation.ThrowIfCancellationRequested();hash.AppendData(id.ToByteArray());}
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private sealed class Summary {
        public int Total,Dvd;
        public readonly HashSet<string> Tags=new(StringComparer.Ordinal);
        public void Add(Episode episode,IReadOnlyDictionary<Guid,BadgeIndexItem> items) {
            Total++;if(episode.Width is >0 and <=1024&&episode.Height is >0 and <=576) Dvd++;
            if(items.TryGetValue(episode.Id,out var facts)) Tags.UnionWith(facts.Labels??[]);
        }
        public BadgeSummary Result()=>new(MediaBadges.DvdMajority(Total,Dvd)?1:0,Tags.Order(StringComparer.Ordinal).Take(64).ToArray());
    }
    public IReadOnlyDictionary<Guid,BadgeSummary> Summaries(IReadOnlyCollection<Guid> containers,Guid userId,IReadOnlyDictionary<Guid,BadgeIndexItem> items,CancellationToken cancellation) {
        var result=new Dictionary<Guid,BadgeSummary>();if(containers.Count==0) return result;
        var user=users.GetUserById(userId);var normal=new Dictionary<Guid,Summary>();
        foreach(Guid id in containers) {
            cancellation.ThrowIfCancellationRequested();var parent=library.GetItemById(id);
            if(user==null||parent is not (Series or Season)||!parent.IsVisibleStandalone(user)) {result[id]=new(0,[]);continue;}
            // Retain native per-container semantics for custom/linked ancestor implementations.
            if(!parent.GetIdsForAncestorQuery().SequenceEqual(new[]{id})) {result[id]=new(Container(id,userId),ContainerTags(id,userId,items,cancellation));continue;}
            normal[id]=new();
        }
        if(normal.Count==0) return result;
        // A single scoped pass supplies both DVD counts and named tags, rather than two count
        // queries plus a second traversal per parent. AncestorIds is Jellyfin's native OR filter.
        for(int offset=0;;offset+=200) {
            cancellation.ThrowIfCancellationRequested();
            var query=new InternalItemsQuery(user) { Recursive=true,AncestorIds=normal.Keys.ToArray(),IncludeItemTypes=[BaseItemKind.Episode],IsVirtualItem=false,IsMissing=false,StartIndex=offset,Limit=200,EnableTotalRecordCount=false };
            var page=library.GetItemList(query);
            foreach(var episode in page.OfType<Episode>()) {
                var parents=new HashSet<Guid> {episode.SeriesId,episode.ParentId,episode.SeasonId};
                if(items.TryGetValue(episode.ParentId,out var season)&&season.Container) parents.UnionWith(season.Parents);
                foreach(Guid id in parents) if(normal.TryGetValue(id,out var summary)) summary.Add(episode,items);
            }
            if(page.Count<200) break;
        }
        foreach(var entry in normal) result[entry.Key]=entry.Value.Result();return result;
    }
    public int Container(Guid id,Guid userId) {
        var user=users.GetUserById(userId);var item=library.GetItemById(id);
        return user!=null&&item!=null&&item.IsVisibleStandalone(user)?MediaBadges.ForContainer(item,user,library):0;
    }
    public string[] ContainerTags(Guid id,Guid userId,IReadOnlyDictionary<Guid,BadgeIndexItem> items,CancellationToken cancellation=default) {
        var user=users.GetUserById(userId);var parent=library.GetItemById(id);if(user==null||parent==null||!parent.IsVisibleStandalone(user)) return [];
        var tags=new HashSet<string>(StringComparer.Ordinal);
        for(int offset=0;;offset+=200) {
            cancellation.ThrowIfCancellationRequested();
            var page=library.GetItemList(new InternalItemsQuery(user) { ParentId=id,Recursive=true,IncludeItemTypes=[BaseItemKind.Episode],IsVirtualItem=false,IsMissing=false,StartIndex=offset,Limit=200,EnableTotalRecordCount=false });
            foreach(var item in page) if(items.TryGetValue(item.Id,out var facts)) tags.UnionWith(facts.Labels??[]);
            if(page.Count<200) break;
        }
        return tags.Order(StringComparer.Ordinal).Take(64).ToArray();
    }
}
