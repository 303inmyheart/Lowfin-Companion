using MediaBrowser.Model.Entities;
using Jellyfin.Data.Enums;
using Xunit;
namespace Lowfin.Companion.Tests;
public sealed class ExpandedBadgesTests {
    [Theory] [InlineData(3840,1600,"4k")] [InlineData(1920,800,"1080p")] [InlineData(1280,720,"720p")] [InlineData(720,576,"dvd")]
    public void ResolutionKeepsCroppedHdDistinctFromDvd(int width,int height,string expected)=>Assert.Contains(expected,ExpandedBadges.Detect([new(){Type=MediaStreamType.Video,Width=width,Height=height}]));
    [Fact] public void CodecAudioLanguagesAndSubtitleIndicatorsAreFactsNotFileNameGuesses() {
        var tags=ExpandedBadges.Detect([
            new(){Type=MediaStreamType.Video,Width=3840,Height=2160,Codec="h265"},
            new(){Type=MediaStreamType.Audio,Codec="truehd",Profile="Atmos",Channels=8,Language="eng"},
            new(){Type=MediaStreamType.Audio,Codec="dts",Profile="DTS-HD MA",Channels=6,Language="ger"},
            new(){Type=MediaStreamType.Subtitle,Language="deu"},new(){Type=MediaStreamType.Subtitle,Language="fre"}
        ],true);
        foreach(string tag in new[]{"4k","hevc","atmos","truehd","dtshdma","7.1","5.1","3d","lang:en","lang:de","vost:fr"}) Assert.Contains(tag,tags);
        Assert.DoesNotContain("vost:de",tags);Assert.DoesNotContain("stereo",tags);
    }
    [Theory] [InlineData(VideoRangeType.DOVI,"dv")] [InlineData(VideoRangeType.HDR10Plus,"hdr10plus")] [InlineData(VideoRangeType.HDR10,"hdr10")] [InlineData(VideoRangeType.HLG,"hlg")] [InlineData(VideoRangeType.SDR,"")]
    public void HdrVariantsRemainDistinct(VideoRangeType type,string expected)=>Assert.Equal(expected,ExpandedBadges.Hdr(VideoRange.SDR,type));
    [Fact] public void SelectedTagsRespectOldMastersAndStereoStartsOff() {
        var config=new PluginConfiguration {ImageBadgesEnabled=true,ShowHdrBadge=false,ShowDvdBadge=false};var enabled=ExpandedBadges.Enabled(config);
        Assert.DoesNotContain("stereo",enabled);Assert.DoesNotContain("dvd",enabled);Assert.DoesNotContain("dv",enabled);Assert.Contains("hevc",enabled);
    }
    [Fact] public void SeriesSummaryKeepsDvdMajorityAndOtherwiseSelectsHighestAvailableResolution() {
        Assert.Contains("dvd",ExpandedBadges.Summarize(["dvd","4k","lang:de"],1));Assert.DoesNotContain("4k",ExpandedBadges.Summarize(["dvd","4k"],1));
        Assert.Equal(new[]{"4k","hevc"},ExpandedBadges.Summarize(["dvd","720p","1080p","4k","hevc"],0));
    }
}
