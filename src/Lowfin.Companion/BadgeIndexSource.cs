using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Lowfin.Companion;

public sealed class BadgeIndexSource(ILibraryManager library,IUserManager users,CacheFeed cache) : IBadgeIndexSource {
    public static bool Supported(BaseItem item)=>item is Video or Series or Season;
    private static BadgeIndexItem Entry(BaseItem item) {
        Guid[] parents=item switch {
            Episode episode=>new[]{episode.SeriesId,episode.ParentId}.Where(x=>x!=Guid.Empty).Distinct().ToArray(),
            Season season=>new[]{season.ParentId}.Where(x=>x!=Guid.Empty).ToArray(),
            _=>[]
        };
        var video=item as Video;var streams=video?.GetMediaSources(false).FirstOrDefault()?.MediaStreams??[];
        return new(item.Id,video==null?0:MediaBadges.Detect(streams),item is Series or Season,parents,video==null?[]:ExpandedBadges.Detect(streams,video.Video3DFormat.HasValue));
    }
    public int? TotalCount()=>library.GetItemsResult(new InternalItemsQuery { Recursive=true,IncludeItemTypes=[BaseItemKind.Movie,BaseItemKind.Video,BaseItemKind.Episode,BaseItemKind.Series,BaseItemKind.Season],IsVirtualItem=false,Limit=0,EnableTotalRecordCount=true }).TotalRecordCount;
    public IEnumerable<BadgeIndexItem> All(CancellationToken cancellation) {
        for(int offset=0;;offset+=200) {
            cancellation.ThrowIfCancellationRequested();
            var page=library.GetItemList(new InternalItemsQuery { Recursive=true,IncludeItemTypes=[BaseItemKind.Movie,BaseItemKind.Video,BaseItemKind.Episode,BaseItemKind.Series,BaseItemKind.Season],IsVirtualItem=false,StartIndex=offset,Limit=200,EnableTotalRecordCount=false });
            foreach(var item in page) { cancellation.ThrowIfCancellationRequested();yield return Entry(item); }
            if(page.Count<200) yield break;
        }
    }
    public BadgeIndexItem? Read(Guid id) { var item=library.GetItemById(id);return item!=null&&Supported(item)&&!item.IsVirtualItem?Entry(item):null; }
    public IReadOnlyList<BadgeIndexUser> Users()=>users.GetUsers().Select(user=>new BadgeIndexUser(user.Id,cache.Scope(user))).ToArray();
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
