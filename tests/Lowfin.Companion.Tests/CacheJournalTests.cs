using Lowfin.Companion;
using Xunit;

namespace Lowfin.Companion.Tests;

public sealed class CacheJournalTests : IDisposable {
    private readonly string directory=Path.Combine(Path.GetTempPath(),"lowfin-cache-"+Guid.NewGuid());
    [Fact] public void MetadataDeliveryCollapsesDuplicateHistoryWithoutSkippingDistinctItemsOrResets() {
        var journal=new CacheJournal(directory);Guid a=Guid.NewGuid(),b=Guid.NewGuid(),c=Guid.NewGuid();
        journal.Append([new(0,a,false,[]),new(0,a,false,[]),new(0,b,false,[]),new(0,a,false,[]),new(0,c,false,[])]);
        var first=journal.CoalescedSnapshot(0,2);Assert.Equal(new[]{b,a},first.Changes.Select(x=>x.ItemId));Assert.Equal(4,first.Changes[^1].Sequence);
        Assert.Equal(c,Assert.Single(journal.CoalescedSnapshot(4,2).Changes).ItemId);Assert.Equal(5,journal.Snapshot().Changes.Count);
        journal.Append([new(0,Guid.Empty,false,[],true)]);Assert.True(Assert.Single(journal.CoalescedSnapshot(0,8).Changes).Reset);
    }
    [Fact] public void DuplicateHistoryWindowIsBoundedAndMakesCursorProgress() {
        var journal=new CacheJournal(directory);Guid id=Guid.NewGuid();journal.Append(Enumerable.Range(0,10000).Select(_=>new CacheChange(0,id,false,[])));
        long cursor=0;int requests=0;while(cursor<10000) {var page=journal.CoalescedSnapshot(cursor,8);Assert.Single(page.Changes);Assert.True(page.Changes[^1].Sequence>cursor);cursor=page.Changes[^1].Sequence;requests++;}Assert.Equal(3,requests);
        Assert.Equal(10000,new CacheJournal(directory).Snapshot().Head);
    }
    [Fact] public void DurableOrderedEntriesSurviveRestart() {
        var journal=new CacheJournal(directory); var id=Guid.NewGuid(); journal.Append([new(0,id,false,[]),new(0,id,false,[])]);
        var restored=new CacheJournal(directory).Snapshot(); Assert.Equal(2,restored.Head); Assert.Equal([1L,2L],restored.Changes.Select(x=>x.Sequence)); Assert.Equal(id,restored.Changes[1].ItemId);
    }
    [Fact] public void RetentionDetectsGapsAndFutureCursors() {
        var journal=new CacheJournal(directory,2); journal.Append(Enumerable.Range(0,4).Select(_=>new CacheChange(0,Guid.NewGuid(),false,[])));
        var state=journal.Snapshot(); Assert.Equal(2,state.Changes.Count); Assert.True(CacheJournal.Expired(state,state.Epoch,1)); Assert.False(CacheJournal.Expired(state,state.Epoch,2)); Assert.True(CacheJournal.Expired(state,state.Epoch,5)); Assert.True(CacheJournal.Expired(state,"different",4));
    }
    [Fact] public async Task WaitingRequestWakesAfterDurableAppend() {
        var journal=new CacheJournal(directory); var waiting=journal.Wait(0,20,CancellationToken.None); Assert.False(waiting.IsCompleted);
        journal.Append([new(0,Guid.NewGuid(),false,[])]); await waiting.WaitAsync(TimeSpan.FromSeconds(2)); Assert.Equal(1,new CacheJournal(directory).Snapshot().Head);
    }
    [Fact] public async Task WaitingRequestCanBeCancelled() {
        var journal=new CacheJournal(directory); using var token=new CancellationTokenSource(); var waiting=journal.Wait(0,20,token.Token); token.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>waiting);
    }
    [Fact] public void CorruptJournalChangesEpochAndCursorScopeIsNotInterchangeable() {
        var journal=new CacheJournal(directory); var state=journal.Snapshot(); string cursor=CacheFeed.Cursor(state,0,"user-a-policy"); Assert.Equal("user-a-policy",CacheFeed.Parse(cursor)!.Scope); Assert.Null(CacheFeed.Parse("invalid"));
        File.WriteAllText(Path.Combine(directory,"cache-journal.json"),"broken"); Assert.NotEqual(state.Epoch,new CacheJournal(directory).Snapshot().Epoch);
    }
    [Fact] public async Task ConcurrentAppendsAreMonotonicAndNeverLoseEntries() {
        var journal=new CacheJournal(directory); await Task.WhenAll(Enumerable.Range(0,25).Select(_=>Task.Run(()=>journal.Append([new(0,Guid.NewGuid(),false,[])])))); var state=journal.Snapshot(); Assert.Equal(25,state.Head); Assert.Equal(Enumerable.Range(1,25).Select(x=>(long)x),state.Changes.Select(x=>x.Sequence));
    }
    [Fact] public async Task LibraryCallbacksCoalesceAndDeletionRequestsSafeReconciliation() {
        var journal=new CacheJournal(directory); var library=new Moq.Mock<MediaBrowser.Controller.Library.ILibraryManager>();
        using var feed=new CacheFeed(journal,library.Object,Microsoft.Extensions.Logging.Abstractions.NullLogger<CacheFeed>.Instance);
        await feed.StartAsync(CancellationToken.None); var item=new MediaBrowser.Controller.Entities.Movies.Movie { Id=Guid.NewGuid() };
        var args=new MediaBrowser.Controller.Library.ItemChangeEventArgs { Item=item };
        library.Raise(x=>x.ItemUpdated+=null,library.Object,args); library.Raise(x=>x.ItemUpdated+=null,library.Object,args);
        await journal.Wait(1,2,CancellationToken.None); Assert.Single(journal.Snapshot().Changes,x=>x.ItemId==item.Id);
        long before=journal.Snapshot().Head; library.Raise(x=>x.ItemRemoved+=null,library.Object,args); await journal.Wait(before,2,CancellationToken.None);
        Assert.True(journal.Snapshot().Changes.Last().Reset); await feed.StopAsync(CancellationToken.None);
    }
    [Fact] public async Task DeliveredNativeRevisionIsNotQueuedAgainButImageAndComputedTagChangesAre() {
        var journal=new CacheJournal(directory);var library=new Moq.Mock<MediaBrowser.Controller.Library.ILibraryManager>();using var feed=new CacheFeed(journal,library.Object,Microsoft.Extensions.Logging.Abstractions.NullLogger<CacheFeed>.Instance);
        await feed.StartAsync(default);var item=new MediaBrowser.Controller.Entities.Movies.Movie {Id=Guid.NewGuid(),DateLastSaved=DateTime.UtcNow};
        var args=new MediaBrowser.Controller.Library.ItemChangeEventArgs {Item=item};library.Raise(x=>x.ItemUpdated+=null,library.Object,args);await journal.Wait(1,2,default);Assert.Equal(2,journal.Snapshot().Head);
        library.Raise(x=>x.ItemUpdated+=null,library.Object,args);await Task.Delay(650);Assert.Equal(2,journal.Snapshot().Head);
        feed.NotifyBadgeChanges([item.Id]);await journal.Wait(2,2,default);Assert.Equal(3,journal.Snapshot().Head);
        args.UpdateReason=MediaBrowser.Controller.Library.ItemUpdateType.ImageUpdate;library.Raise(x=>x.ItemUpdated+=null,library.Object,args);await journal.Wait(3,2,default);Assert.Equal(4,journal.Snapshot().Head);
        args.UpdateReason=MediaBrowser.Controller.Library.ItemUpdateType.None;item.DateLastSaved=item.DateLastSaved.AddSeconds(1);library.Raise(x=>x.ItemUpdated+=null,library.Object,args);await journal.Wait(4,2,default);Assert.Equal(5,journal.Snapshot().Head);await feed.StopAsync(default);
    }
    [Fact] public void DefaultHistoryRetainsFarMoreThanTheOldLimitAndPagesWithoutFalseGaps() {
        Assert.Equal(250000,CacheJournal.DefaultCapacity);var journal=new CacheJournal(directory);
        journal.Append(Enumerable.Range(0,12000).Select(_=>new CacheChange(0,Guid.NewGuid(),false,[])));
        var restored=new CacheJournal(directory);var page=restored.Snapshot(4090,8);Assert.Equal(8,page.Changes.Count);Assert.Equal(4091,page.Changes[0].Sequence);Assert.Equal(12000,page.Head);Assert.False(CacheJournal.Expired(page,page.Epoch,4090));
        var all=restored.Snapshot();Assert.Equal(12000,all.Changes.Count);Assert.False(CacheJournal.Expired(all,all.Epoch,0));
    }
    [Fact] public void DefaultCapacityBoundaryRetainsExactlyThePromisedHistory() {
        var journal=new CacheJournal(directory);journal.Append(Enumerable.Range(0,CacheJournal.DefaultCapacity+3).Select(_=>new CacheChange(0,Guid.NewGuid(),false,[])));
        var state=journal.Snapshot(0,8);Assert.Equal(4,state.FirstSequence);Assert.True(CacheJournal.Expired(state,state.Epoch,2));Assert.False(CacheJournal.Expired(state,state.Epoch,3));Assert.Equal(250000,new CacheJournal(directory).Snapshot().Changes.Count);
    }
    [Fact] public void IdleAppendsDoNotRewriteSnapshotOrAppendToLog() {
        var journal=new CacheJournal(directory);journal.Append([new(0,Guid.NewGuid(),false,[])]);
        string snapshot=File.ReadAllText(Path.Combine(directory,"cache-journal.json"));long bytes=new FileInfo(Path.Combine(directory,"cache-journal.wal")).Length;
        for(int i=0;i<10;i++)journal.Append([]);Assert.Equal(snapshot,File.ReadAllText(Path.Combine(directory,"cache-journal.json")));Assert.Equal(bytes,new FileInfo(Path.Combine(directory,"cache-journal.wal")).Length);
    }
    [Fact] public void LegacySnapshotKeepsEpochAndCursorWhenNewLogIsAdded() {
        string epoch=Guid.NewGuid().ToString("N");Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory,"cache-journal.json"),System.Text.Json.JsonSerializer.Serialize(new CacheJournalState(epoch,1,[new(1,Guid.NewGuid(),false,[])])));
        var journal=new CacheJournal(directory);journal.Append([new(0,Guid.NewGuid(),false,[])]);var restored=new CacheJournal(directory).Snapshot();Assert.Equal(epoch,restored.Epoch);Assert.Equal(2,restored.Head);Assert.False(CacheJournal.Expired(restored,epoch,1));
    }
    [Fact] public void TruncatedUnacknowledgedTailIsDiscardedAndFutureAppendsRemainReadable() {
        var journal=new CacheJournal(directory);journal.Append([new(0,Guid.NewGuid(),false,[])]);string wal=Path.Combine(directory,"cache-journal.wal");long durable=new FileInfo(wal).Length;
        using(var stream=new FileStream(wal,FileMode.Append))stream.Write([64,0,0,0,123,34]);
        var recovered=new CacheJournal(directory);Assert.Equal(1,recovered.Snapshot().Head);Assert.Equal(durable,new FileInfo(wal).Length);
        recovered.Append([new(0,Guid.NewGuid(),false,[])]);Assert.Equal(2,new CacheJournal(directory).Snapshot().Head);
    }
    [Fact] public void CompleteCorruptLogCannotMasqueradeAsAnIntactHistory() {
        var journal=new CacheJournal(directory);journal.Append([new(0,Guid.NewGuid(),false,[])]);string epoch=journal.Snapshot().Epoch;
        string wal=Path.Combine(directory,"cache-journal.wal");byte[] bytes=File.ReadAllBytes(wal);bytes[^1]^=1;File.WriteAllBytes(wal,bytes);Assert.NotEqual(epoch,new CacheJournal(directory).Snapshot().Epoch);
    }
    [Fact] public void ReplayedCheckpointedLogDoesNotDuplicateOrLoseSequences() {
        var journal=new CacheJournal(directory);journal.Append([new(0,Guid.NewGuid(),false,[])]);byte[] oldLog=File.ReadAllBytes(Path.Combine(directory,"cache-journal.wal"));
        File.WriteAllText(Path.Combine(directory,"cache-journal.json"),System.Text.Json.JsonSerializer.Serialize(journal.Snapshot()));File.WriteAllBytes(Path.Combine(directory,"cache-journal.wal"),oldLog);
        var restored=new CacheJournal(directory);restored.Append([new(0,Guid.NewGuid(),false,[])]);Assert.Equal([1L,2L],new CacheJournal(directory).Snapshot().Changes.Select(change=>change.Sequence));
    }
    public void Dispose() { if(Directory.Exists(directory)) Directory.Delete(directory,true); }
}
