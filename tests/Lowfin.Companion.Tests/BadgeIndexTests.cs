using Xunit;

namespace Lowfin.Companion.Tests;

public sealed class BadgeIndexTests : IDisposable {
    private readonly string directory=Path.Combine(Path.GetTempPath(),"lowfin-index-test-"+Guid.NewGuid());
    private readonly Guid movie=Guid.NewGuid(),series=Guid.NewGuid(),season=Guid.NewGuid(),user=Guid.NewGuid();
    private sealed class Clock : TimeProvider { public DateTimeOffset Now=new(2026,10,21,12,0,0,TimeSpan.Zero);public override DateTimeOffset GetUtcNow()=>Now; }
    private sealed class Source : IBadgeIndexSource {
        public Dictionary<Guid,BadgeIndexItem> Items=[];public List<BadgeIndexUser> Accounts=[];public Dictionary<Guid,int> Masks=[];
        public int Reads,Counts,Scans;public Action? DuringScan;
        public IEnumerable<BadgeIndexItem> All(CancellationToken token) { Scans++;var snapshot=Items.Values.ToArray();DuringScan?.Invoke();foreach(var item in snapshot) { token.ThrowIfCancellationRequested();yield return item; } }
        public int? TotalCount()=>Items.Count;
        public BadgeIndexItem? Read(Guid id) { Reads++;return Items.GetValueOrDefault(id); }
        public IReadOnlyList<BadgeIndexUser> Users()=>Accounts;
        public int Container(Guid id,Guid user) { Counts++;return Masks.GetValueOrDefault(id); }
    }
    private Source Fixture()=>new() { Items={ [movie]=new(movie,3,false,[series,season]),[series]=new(series,0,true,[]),[season]=new(season,0,true,[series]) },Accounts=[new(user,"scope-a")],Masks={ [series]=1,[season]=1 } };
    [Fact] public async Task PersistedSnapshotServesWarmRequestsWithoutAnySourceQueries() {
        var source=Fixture();var index=new BadgeIndex(directory,source,_=>{});await index.Rebuild(default);
        var restored=new BadgeIndex(directory,source,_=>{});int reads=source.Reads,counts=source.Counts,scans=source.Scans;
        for(int i=0;i<100;i++) { Assert.Equal(3,restored.Get(movie,user,"scope-a",false));Assert.Equal(1,restored.Get(series,user,"scope-a",true)); }
        Assert.Equal((reads,counts,scans),(source.Reads,source.Counts,source.Scans));
    }
    [Fact] public async Task EpisodeChangesRecomputeOnlyAffectedParentsAndNotifyAfterPublication() {
        var source=Fixture();var clock=new Clock();var notified=new HashSet<Guid>();BadgeIndex? index=null;
        index=new(directory,source,ids=> { Assert.Equal(4,index!.Get(movie,user,"scope-a",false));notified.UnionWith(ids); },clock);
        await index.Rebuild(default);source.Items[movie]=new(movie,4,false,[series,season]);source.Masks[series]=0;source.Masks[season]=0;int before=source.Counts;
        clock.Now=clock.Now.AddSeconds(31);index.Queue(movie);await index.Update(default);
        Assert.Equal(2,source.Counts-before);Assert.Equal(0,index.Get(series,user,"scope-a",true));Assert.True(notified.SetEquals([movie,series,season]));
        var restored=new BadgeIndex(directory,source,_=>{},clock);Assert.Equal(4,restored.Get(movie,user,"scope-a",false));
    }
    [Fact] public async Task RemovedEpisodeUsesStoredParentMembershipToRefreshMajority() {
        var source=Fixture();var notified=new HashSet<Guid>();var index=new BadgeIndex(directory,source,ids=>notified.UnionWith(ids));await index.Rebuild(default);
        source.Items.Remove(movie);source.Masks[series]=0;source.Masks[season]=0;index.Queue(movie);await index.Update(default);
        Assert.Equal(0,index.Get(series,user,"scope-a",true));Assert.Equal(0,index.Get(season,user,"scope-a",true));Assert.Contains(series,notified);Assert.Null(index.Get(movie,user,"scope-a",false));
    }
    [Fact] public async Task ChangedPermissionsNeverReturnAnotherScopeAndAreRebuiltOffRequestPath() {
        var source=Fixture();var index=new BadgeIndex(directory,source,_=>{});await index.Rebuild(default);int counts=source.Counts;
        source.Accounts=[new(user,"scope-b")];source.Masks[series]=0;
        Assert.Null(index.Get(series,user,"scope-b",true));Assert.Equal(counts,source.Counts);await index.Update(default);
        Assert.Equal(0,index.Get(series,user,"scope-b",true));Assert.Null(index.Get(series,user,"scope-a",true));
    }
    [Fact] public async Task EventsArrivingDuringFullScanAreNotLostOrOverwrittenByOldSnapshot() {
        var source=Fixture();var index=new BadgeIndex(directory,source,_=>{});using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        source.DuringScan=()=> { entered.Set();Assert.True(release.Wait(TimeSpan.FromSeconds(5))); };
        var build=Task.Run(()=>index.Rebuild(default));Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        source.Items[movie]=new(movie,4,false,[series,season]);index.Queue(movie);release.Set();await build;
        Assert.Equal(3,index.Get(movie,user,"scope-a",false));await index.Update(default);Assert.Equal(4,index.Get(movie,user,"scope-a",false));
    }
    [Fact] public async Task CancellationRetainsPreviousCompleteSnapshot() {
        var source=Fixture();var index=new BadgeIndex(directory,source,_=>{});await index.Rebuild(default);
        using var cancellation=new CancellationTokenSource();source.DuringScan=()=>cancellation.Cancel();source.Items[movie]=new(movie,0,false,[]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>index.Rebuild(cancellation.Token));Assert.Equal(3,index.Get(movie,user,"scope-a",false));Assert.True(index.FullRequested);
        var restored=new BadgeIndex(directory,source,_=>{});Assert.Equal(3,restored.Get(movie,user,"scope-a",false));
    }
    [Fact] public async Task DailyCheckUsesDurableTimestampAndTaskHasTwentyFourHourTrigger() {
        var source=Fixture();var clock=new Clock();var index=new BadgeIndex(directory,source,_=>{},clock);Assert.True(index.Due);await index.Rebuild(default);
        clock.Now=clock.Now.AddHours(23);Assert.False(new BadgeIndex(directory,source,_=>{},clock).Due);clock.Now=clock.Now.AddHours(1);Assert.True(index.Due);
        var task=new BadgeIndexTask(index);Assert.Equal(TimeSpan.FromHours(24).Ticks,Assert.Single(task.GetDefaultTriggers()).IntervalTicks);
    }
    [Fact] public async Task CorruptOrMissingIndexQueuesWorkAndNeverCalculatesInRead() {
        Directory.CreateDirectory(directory);File.WriteAllText(Path.Combine(directory,"badge-index.json"),"broken");var source=Fixture();var index=new BadgeIndex(directory,source,_=>{});
        Assert.False(index.Ready);Assert.Null(index.Get(movie,user,"scope-a",false));Assert.Equal((0,0,0),(source.Reads,source.Counts,source.Scans));await index.Rebuild(default);Assert.True(index.Ready);
    }
    [Fact] public async Task CheckpointsAreCoalescedAndFlushPersistsLatestIncrementalFacts() {
        var source=Fixture();var clock=new Clock();var index=new BadgeIndex(directory,source,_=>{},clock);await index.Rebuild(default);
        source.Items[movie]=new(movie,7,false,[series,season]);index.Queue(movie);await index.Update(default);Assert.Equal(7,index.Get(movie,user,"scope-a",false));
        Assert.Equal(3,new BadgeIndex(directory,source,_=>{},clock).Get(movie,user,"scope-a",false));await index.Flush(default);Assert.Equal(7,new BadgeIndex(directory,source,_=>{},clock).Get(movie,user,"scope-a",false));
    }
    [Fact] public async Task NamedFactsPersistAndLabelOnlyChangesNotifyWithoutLosingExistingMasks() {
        var source=Fixture();source.Items[movie]=source.Items[movie] with {Labels=["4k","hevc"]};var notified=new HashSet<Guid>();var index=new BadgeIndex(directory,source,ids=>notified.UnionWith(ids));await index.Rebuild(default);
        Assert.Equal(new[]{"4k","hevc"},index.GetLabels(movie,user,"scope-a",false));
        source.Items[movie]=source.Items[movie] with {Labels=["1080p","h264"]};index.Queue(movie);await index.Update(default);await index.Flush(default);
        Assert.Contains(movie,notified);var restored=new BadgeIndex(directory,source,_=>{});Assert.Equal(new[]{"1080p","h264"},restored.GetLabels(movie,user,"scope-a",false));Assert.Equal(3,restored.Get(movie,user,"scope-a",false));
    }
    [Fact] public async Task UpgradingOldIndexDoesNotFloodMetadataJournalWithLabelOnlyMigration() {
        var source=Fixture();var notified=new HashSet<Guid>();var index=new BadgeIndex(directory,source,ids=>notified.UnionWith(ids));await index.Rebuild(default);
        source.Items[movie]=source.Items[movie] with {Labels=["dvd","h264"]};await index.Rebuild(default);Assert.Empty(notified);Assert.Equal(new[]{"dvd","h264"},index.GetLabels(movie,user,"scope-a",false));
    }
    private sealed class Progress(Action<double> report) : IProgress<double> { public void Report(double value)=>report(value); }
    [Fact] public async Task RebuildReportsIntermediatePhasesAndOnlyCompletesAfterSnapshotIsReadable() {
        var source=Fixture();var index=new BadgeIndex(directory,source,_=>{});var values=new List<double>();
        await index.Rebuild(default,new Progress(value=> {
            values.Add(value);
            if(value==100) Assert.Equal(3,index.Get(movie,user,"scope-a",false));
        }));
        Assert.Contains(55d,values);Assert.Contains(99d,values);Assert.Equal(100d,values[^1]);
        Assert.True(values.SequenceEqual(values.OrderBy(value=>value)));
    }
    [Fact] public async Task ScheduledCallerWaitingForBackgroundBuildGetsProgressAndDoesNotRebuildAgain() {
        var source=Fixture();var index=new BadgeIndex(directory,source,_=>{});
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        source.DuringScan=()=> { entered.Set();Assert.True(release.Wait(TimeSpan.FromSeconds(10))); };
        var background=Task.Run(()=>index.Rebuild(default));Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var values=new List<double>();var waiting=index.Rebuild(default,new Progress(value=>values.Add(value)));
        try { await Task.Delay(1200);Assert.NotEmpty(values);Assert.False(waiting.IsCompleted); }
        finally { release.Set(); }
        await background;await waiting;Assert.Equal(1,source.Scans);Assert.Equal(100d,values[^1]);
    }
    public void Dispose() { if(Directory.Exists(directory)) Directory.Delete(directory,true); }
}
