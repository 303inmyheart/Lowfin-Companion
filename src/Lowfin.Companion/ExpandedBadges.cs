using System.Globalization;
using MediaBrowser.Model.Entities;
using Jellyfin.Data.Enums;

namespace Lowfin.Companion;

/// <summary>JellyTag-compatible tag categories as compact facts; no server-side artwork rendering.</summary>
public static class ExpandedBadges {
    public static readonly string[] Keys=["dvd","5.1","hdr","4k","1080p","720p","hdr10","hdr10plus","dv","hlg","3d","h264","hevc","av1","vp9","atmos","dtsx","truehd","dtshdma","7.1","stereo","languages","subtitles"];
    public static string[] Enabled(PluginConfiguration? config) {
        if(config?.ImageBadgesEnabled!=true) return [];
        var selected=new HashSet<string>(config.EnabledCoverTags??Keys,StringComparer.Ordinal);
        if(!config.ShowDvdBadge) selected.Remove("dvd");if(!config.ShowSurroundBadge) selected.Remove("5.1");
        if(!config.ShowHdrBadge) foreach(string key in new[]{"hdr","hdr10","hdr10plus","dv","hlg"}) selected.Remove(key);
        return Keys.Where(selected.Contains).ToArray();
    }
    private static readonly Dictionary<string,string> Languages=BuildLanguages();
    private static Dictionary<string,string> BuildLanguages() {
        var result=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(var culture in CultureInfo.GetCultures(CultureTypes.NeutralCultures)) if(culture.TwoLetterISOLanguageName.Length==2) {
            result[culture.ThreeLetterISOLanguageName]=culture.TwoLetterISOLanguageName;result[culture.TwoLetterISOLanguageName]=culture.TwoLetterISOLanguageName;
        }
        foreach(var pair in new Dictionary<string,string>{{"ger","de"},{"fre","fr"},{"dut","nl"},{"cze","cs"},{"rum","ro"},{"chi","zh"},{"gre","el"},{"may","ms"},{"slo","sk"},{"baq","eu"},{"wel","cy"}}) result[pair.Key]=pair.Value;
        return result;
    }
    public static string Language(string? value) {
        string code=(value??"").Trim().ToLowerInvariant().Split('-')[0];
        if(code is "und" or "zxx"||code.Length is <2 or >3||!code.All(c=>c is >= 'a' and <= 'z')) return "";
        return Languages.GetValueOrDefault(code,code);
    }
    public static string Hdr(VideoRange range,VideoRangeType type)=>type.ToString().StartsWith("DOVI",StringComparison.Ordinal)?"dv":type switch {VideoRangeType.HDR10Plus=>"hdr10plus",VideoRangeType.HDR10=>"hdr10",VideoRangeType.HLG=>"hlg",_=>range==VideoRange.HDR?"hdr":""};
    public static string[] Detect(IEnumerable<MediaStream> streams,bool threeD=false) {
        var all=streams.ToArray();var result=new List<string>();var video=all.FirstOrDefault(x=>x.Type==MediaStreamType.Video);
        if(video!=null) {
            int width=video.Width??0,height=video.Height??0;
            if(width>0&&height>0) {
                if(width<=1024&&height<=576) result.Add("dvd");
                else if(width>=3800||height>=2000) result.Add("4k");
                else if(width>=1800||height>=1000) result.Add("1080p");
                else if(width>=1200||height>=700) result.Add("720p");
            }
            string hdr=Hdr(video.VideoRange,video.VideoRangeType);if(hdr.Length>0) result.Add(hdr);
            string codec=(video.Codec??"").ToLowerInvariant();string tag=codec switch {"h264" or "avc"=>"h264","hevc" or "h265"=>"hevc","av1"=>"av1","vp9"=>"vp9",_=>""};if(tag.Length>0) result.Add(tag);
        }
        if(threeD) result.Add("3d");
        var audio=all.Where(x=>x.Type==MediaStreamType.Audio).ToArray();
        foreach(var track in audio) {
            string codec=(track.Codec??"").ToUpperInvariant(),profile=(track.Profile??"").ToUpperInvariant();
            // Do not infer Atmos/DTS:X from a generic container or ordinary codec name.
            if(profile.Contains("ATMOS")||profile.Contains("JOC")) result.Add("atmos");
            if(profile.Contains("DTS:X")||profile.Contains("DTS-X")||profile.Contains("DTSX")) result.Add("dtsx");
            if(codec=="TRUEHD") result.Add("truehd");
            if(profile.Contains("DTS-HD MA")||profile.Contains("DTS-HD MASTER")||(codec=="DTS"&&(profile=="MA"||profile.Contains("DTSHDMA")))) result.Add("dtshdma");
            if(track.Channels==8) result.Add("7.1");else if(track.Channels==6) result.Add("5.1");else if(track.Channels==2) result.Add("stereo");
        }
        var languages=audio.Select(x=>Language(x.Language)).Where(x=>x.Length>0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        result.AddRange(languages.Select(x=>"lang:"+x));
        result.AddRange(all.Where(x=>x.Type==MediaStreamType.Subtitle).Select(x=>Language(x.Language)).Where(x=>x.Length>0&&!languages.Contains(x,StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(x=>"vost:"+x));
        return result.Distinct(StringComparer.Ordinal).Take(32).ToArray();
    }
    public static string[] Summarize(IEnumerable<string> labels,int dvdMask) {
        var all=new HashSet<string>(labels,StringComparer.Ordinal);var result=new List<string>();
        if((dvdMask&1)!=0) result.Add("dvd");else foreach(string resolution in new[]{"4k","1080p","720p"}) if(all.Contains(resolution)) {result.Add(resolution);break;}
        result.AddRange(all.Where(x=>x is not ("dvd" or "4k" or "1080p" or "720p")).Order(StringComparer.Ordinal));return result.Take(32).ToArray();
    }
}
