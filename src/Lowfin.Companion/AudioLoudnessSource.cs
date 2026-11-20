using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace Lowfin.Companion;

public sealed record LoudnessCandidate(Guid Id,IReadOnlyList<LoudnessTrack>? Tracks=null);
public interface ILoudnessSource {
    IEnumerable<Guid> All(CancellationToken cancellation);
    IReadOnlyList<LoudnessTrack> Tracks(Guid id);
    int? TotalCount()=>null;
    IEnumerable<LoudnessCandidate> Candidates(CancellationToken cancellation)=>All(cancellation).ToArray().Select(id=>new LoudnessCandidate(id));
}
public sealed class AudioLoudnessSource(ILibraryManager library) : ILoudnessSource {
    public int? TotalCount()=>library.GetItemsResult(new InternalItemsQuery {Recursive=true,IncludeItemTypes=[BaseItemKind.Movie,BaseItemKind.Episode,BaseItemKind.Video],IsVirtualItem=false,Limit=0,EnableTotalRecordCount=true}).TotalRecordCount;
    public IEnumerable<Guid> All(CancellationToken cancellation)=>Candidates(cancellation).Select(candidate=>candidate.Id);
    public IEnumerable<LoudnessCandidate> Candidates(CancellationToken cancellation) {
        for(int offset=0;;offset+=200) {
            cancellation.ThrowIfCancellationRequested();
            var page=library.GetItemList(new InternalItemsQuery { Recursive=true,IncludeItemTypes=[BaseItemKind.Movie,BaseItemKind.Episode,BaseItemKind.Video],IsVirtualItem=false,StartIndex=offset,Limit=200,EnableTotalRecordCount=false });
            foreach(var item in page) {cancellation.ThrowIfCancellationRequested();yield return new(item.Id,Tracks(item as Video));}if(page.Count<200) yield break;
        }
    }
    public IReadOnlyList<LoudnessTrack> Tracks(Guid id)=>Tracks(library.GetItemById(id) as Video);
    private static IReadOnlyList<LoudnessTrack> Tracks(Video? video) {
        if(video==null||video.IsVirtualItem) return [];
        return video.GetMediaSources(false).Where(source=>source.Protocol==MediaProtocol.File&&!string.IsNullOrWhiteSpace(source.Id)&&System.IO.Path.IsPathFullyQualified(source.Path??""))
            .SelectMany(source=>source.MediaStreams.Where(track=>track.Type==MediaStreamType.Audio&&!track.IsExternal&&track.Index>=0&&track.Channels>0).Select(track=>new LoudnessTrack(video.Id,source.Id,track.Index,source.Path,track.Channels??0,track.ChannelLayout??"",track.Codec??"",track.SampleRate??0,source.RunTimeTicks??0))).ToArray();
    }
}
