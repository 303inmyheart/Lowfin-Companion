using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Moq;
using Xunit;

namespace Lowfin.Companion.Tests;

public sealed class BadgeIndexSourceTests {
    private sealed class VisibleSeries : Series {public override bool IsVisibleStandalone(User user)=>true;}
    private sealed class VisibleSeason : Season {public override bool IsVisibleStandalone(User user)=>true;}
    private sealed class HiddenSeason : Season {public override bool IsVisibleStandalone(User user)=>false;}
    [Fact] public void ManyParentsShareOneScopedEpisodePassForCountsAndTags() {
        var user=new User("test","auth","reset");var manager=new Mock<IUserManager>();manager.Setup(x=>x.GetUserById(user.Id)).Returns(user);
        var series=new VisibleSeries {Id=Guid.NewGuid()};var season=new VisibleSeason {Id=Guid.NewGuid(),ParentId=series.Id};var hidden=new HiddenSeason {Id=Guid.NewGuid(),ParentId=series.Id};
        Episode episode(int width)=>new() {Id=Guid.NewGuid(),ParentId=season.Id,SeasonId=season.Id,SeriesId=series.Id,Width=width,Height=576};
        var episodes=new[]{episode(720),episode(1024),episode(1920)};var library=new Mock<ILibraryManager>();
        foreach(var item in new BaseItem[]{series,season,hidden}) library.Setup(x=>x.GetItemById(item.Id)).Returns(item);
        int queries=0;library.Setup(x=>x.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery q)=> {
            queries++;Assert.Same(user,q.User);Assert.True(q.Recursive);Assert.False(q.IsMissing);Assert.False(q.IsVirtualItem);Assert.False(q.EnableTotalRecordCount);Assert.Equal(200,q.Limit);
            Assert.True(q.AncestorIds.ToHashSet().SetEquals(new[]{series.Id,season.Id}));Assert.DoesNotContain(hidden.Id,q.AncestorIds);
            return episodes.Cast<BaseItem>().ToArray();
        });
        var facts=episodes.ToDictionary(e=>e.Id,e=>new BadgeIndexItem(e.Id,0,false,[series.Id,season.Id],["h264","5.1"]));
        var source=new BadgeIndexSource(library.Object,manager.Object,null!);var result=source.Summaries([series.Id,season.Id,hidden.Id],user.Id,facts,default);
        Assert.Equal(1,queries);Assert.Equal(1,result[series.Id].Mask);Assert.Equal(1,result[season.Id].Mask);Assert.Equal(0,result[hidden.Id].Mask);Assert.Empty(result[hidden.Id].Tags);Assert.Contains("5.1",result[series.Id].Tags);
        library.Verify(x=>x.GetItemsResult(It.IsAny<InternalItemsQuery>()),Times.Never);
    }
    [Fact] public void WarmMediaScanUsesNativeRevisionWithoutResolvingAnyMediaSources() {
        var video=new Video {Id=Guid.NewGuid(),DateLastSaved=DateTime.UtcNow,DateModified=DateTime.UtcNow,Width=1920,Height=1080};
        var cached=new BadgeIndexItem(video.Id,0,false,[],["1080p"],BadgeIndexSource.Revision(video),"access");var library=new Mock<ILibraryManager>();
        library.Setup(x=>x.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(new BaseItem[]{video});
        var source=new BadgeIndexSource(library.Object,new Mock<IUserManager>().Object,null!);
        // No MediaSourceManager is installed in this fixture: a redundant source lookup would fail.
        Assert.Same(cached,Assert.Single(source.All(new Dictionary<Guid,BadgeIndexItem>{{video.Id,cached}},default)));
    }
    [Fact] public void VisibilityFingerprintUsesNativeUserFiltersAndChangesWhenVisibleItemsChange() {
        var user=new User("test","auth","reset");var manager=new Mock<IUserManager>();manager.Setup(x=>x.GetUserById(user.Id)).Returns(user);
        Guid[] visible=[Guid.NewGuid(),Guid.NewGuid()];var library=new Mock<ILibraryManager>();
        library.Setup(x=>x.GetItemIds(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery q)=> {Assert.Same(user,q.User);Assert.True(q.Recursive);Assert.False(q.IsVirtualItem);return visible;});
        var source=new BadgeIndexSource(library.Object,manager.Object,null!);string? before=source.Visibility(user.Id,default);
        Array.Reverse(visible);Assert.Equal(before,source.Visibility(user.Id,default));visible=[visible[0]];Assert.NotEqual(before,source.Visibility(user.Id,default));
    }
}
