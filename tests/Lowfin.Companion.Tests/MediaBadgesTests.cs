using MediaBrowser.Model.Entities;
using Xunit;
namespace Lowfin.Companion.Tests;
public sealed class MediaBadgesTests {
    [Theory]
    [InlineData(720,576,true)] [InlineData(720,480,true)] [InlineData(1024,576,true)]
    [InlineData(1920,540,false)] [InlineData(1280,720,false)] [InlineData(1920,1080,false)]
    [InlineData(3840,2160,false)] [InlineData(0,0,false)]
    public void DvdUsesSdDimensionsWithoutMislabelingCroppedHd(int width,int height,bool expected) {
        Assert.Equal(expected,(MediaBadges.Detect([new() { Type=MediaStreamType.Video,Width=width,Height=height }])&1)!=0);
    }
    [Theory] [InlineData(1,false)] [InlineData(2,false)] [InlineData(6,true)] [InlineData(8,false)]
    public void SurroundRequiresAnActualSixChannelTrack(int channels,bool expected) {
        Assert.Equal(expected,(MediaBadges.Detect([new() { Type=MediaStreamType.Audio,Channels=channels }])&2)!=0);
    }
    [Fact] public void AnySixChannelTrackCountsAndEmptyUnknownDataDoesNotInventTags() {
        Assert.Equal(3,MediaBadges.Detect([new() { Type=MediaStreamType.Video,Width=720,Height=576 },new() { Type=MediaStreamType.Audio,Channels=2 },new() { Type=MediaStreamType.Audio,Channels=6 }]));
        Assert.Equal(0,MediaBadges.Detect([]));
        Assert.Equal(0,MediaBadges.ForItem(new MediaBrowser.Controller.Entities.TV.Series()));
    }
    [Theory]
    [InlineData(Jellyfin.Data.Enums.VideoRangeType.HDR10,true)]
    [InlineData(Jellyfin.Data.Enums.VideoRangeType.HDR10Plus,true)]
    [InlineData(Jellyfin.Data.Enums.VideoRangeType.HLG,true)]
    [InlineData(Jellyfin.Data.Enums.VideoRangeType.DOVI,true)]
    [InlineData(Jellyfin.Data.Enums.VideoRangeType.SDR,false)]
    public void HdrUsesActualVideoRange(Jellyfin.Data.Enums.VideoRangeType range,bool expected) {
        Assert.Equal(expected,MediaBadges.IsHdr(Jellyfin.Data.Enums.VideoRange.SDR,range));
    }
    [Theory] [InlineData(10,6,true)] [InlineData(10,5,false)] [InlineData(3,2,true)] [InlineData(0,0,false)] [InlineData(5,0,false)]
    public void MixedQualityUsesStrictMajorityOfAllEpisodes(int total,int dvd,bool expected)=>Assert.Equal(expected,MediaBadges.DvdMajority(total,dvd));
    [Fact] public void SeriesAndSeasonsUseTwoScopedCountQueriesInsteadOfLoadingEpisodes() {
        var user=new Jellyfin.Database.Implementations.Entities.User("user","auth","reset");var parent=new MediaBrowser.Controller.Entities.TV.Season { Id=Guid.NewGuid() };
        var library=new Moq.Mock<MediaBrowser.Controller.Library.ILibraryManager>();
        library.Setup(x=>x.GetItemsResult(Moq.It.IsAny<MediaBrowser.Controller.Entities.InternalItemsQuery>())).Returns((MediaBrowser.Controller.Entities.InternalItemsQuery q)=> {
            Assert.Equal(parent.Id,q.ParentId);Assert.True(q.Recursive);Assert.Equal(0,q.Limit);Assert.False(q.IsVirtualItem);Assert.False(q.IsMissing);
            return new MediaBrowser.Model.Querying.QueryResult<MediaBrowser.Controller.Entities.BaseItem> { Items=[],TotalRecordCount=q.MaxHeight==576?61:100 };
        });
        Assert.Equal(1,MediaBadges.ForContainer(parent,user,library.Object));library.Verify(x=>x.GetItemsResult(Moq.It.IsAny<MediaBrowser.Controller.Entities.InternalItemsQuery>()),Moq.Times.Exactly(2));
    }
    [Fact] public void ExistingInstallationsDoNotEnableTagsImplicitly() {
        var config=new PluginConfiguration(); Assert.False(config.ImageBadgesEnabled); Assert.True(config.ShowDvdBadge); Assert.True(config.ShowSurroundBadge);
    }
}
