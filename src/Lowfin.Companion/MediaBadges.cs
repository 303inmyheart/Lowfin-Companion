using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Data.Enums;

namespace Lowfin.Companion;

/// <summary>Frame-relative badge facts for Lowfin. Source images and Jellyfin image endpoints stay untouched.</summary>
public static class MediaBadges {
    public static object Settings(PluginConfiguration? config)=>new { enabled=config?.ImageBadgesEnabled==true,dvd=config?.ShowDvdBadge!=false,surround=config?.ShowSurroundBadge!=false,hdr=config?.ShowHdrBadge!=false,version=1,labelVersion=1,tags=ExpandedBadges.Enabled(config) };
    public static int Detect(IEnumerable<MediaStream> streams) {
        var all=streams.ToArray(); var video=all.FirstOrDefault(s=>s.Type==MediaStreamType.Video);
        // Cropped ultrawide HD must not become DVD just because its height is below 576.
        bool dvd=video?.Width is >0 and <=1024 && video.Height is >0 and <=576;
        bool surround=all.Any(s=>s.Type==MediaStreamType.Audio && s.Channels==6);
        bool hdr=video!=null&&IsHdr(video.VideoRange,video.VideoRangeType);
        return (dvd?1:0)|(surround?2:0)|(hdr?4:0);
    }
    public static bool IsHdr(VideoRange range,VideoRangeType type)=>range==VideoRange.HDR||type.ToString().StartsWith("DOVI",StringComparison.Ordinal)||type is VideoRangeType.HDR10 or VideoRangeType.HDR10Plus or VideoRangeType.HLG;
    public static bool DvdMajority(int total,int dvd)=>total>0&&dvd>total/2;
    public static int ForContainer(BaseItem item,User user,ILibraryManager library) {
        if(item is not (Series or Season)) return 0;
        var all=new InternalItemsQuery(user) { ParentId=item.Id,Recursive=true,IncludeItemTypes=[BaseItemKind.Episode],IsVirtualItem=false,IsMissing=false,Limit=0,EnableTotalRecordCount=true };
        int total=library.GetItemsResult(all).TotalRecordCount;if(total==0) return 0;
        var sd=new InternalItemsQuery(user) { ParentId=item.Id,Recursive=true,IncludeItemTypes=[BaseItemKind.Episode],IsVirtualItem=false,IsMissing=false,Limit=0,EnableTotalRecordCount=true,MinWidth=1,MaxWidth=1024,MinHeight=1,MaxHeight=576 };
        return DvdMajority(total,library.GetItemsResult(sd).TotalRecordCount)?1:0;
    }
    public static int ForItem(BaseItem item)=>item is Video video
        ? Detect(video.GetMediaSources(false).FirstOrDefault()?.MediaStreams ?? []) : 0;
}
