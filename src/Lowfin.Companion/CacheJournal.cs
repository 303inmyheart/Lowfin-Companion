using System.Text.Json;

namespace Lowfin.Companion;

public sealed record CacheChange(long Sequence,Guid ItemId,bool Removed,Guid[] Libraries,bool Reset=false);
public sealed record CacheJournalState(string Epoch,long Head,List<CacheChange> Changes);

/// <summary>Bounded atomic journal. A cursor is published only after its entries are durable.</summary>
public sealed class CacheJournal {
    private readonly object gate=new();
    private readonly string path;
    private readonly int capacity;
    private CacheJournalState state;
    private TaskCompletionSource signal=NewSignal();
    private static TaskCompletionSource NewSignal()=>new(TaskCreationOptions.RunContinuationsAsynchronously);
    public CacheJournal(string directory,int capacity=4096) {
        Directory.CreateDirectory(directory); path=Path.Combine(directory,"cache-journal.json"); this.capacity=capacity;
        try { state=JsonSerializer.Deserialize<CacheJournalState>(File.ReadAllText(path))??throw new IOException(); }
        catch(Exception e) when(e is IOException or JsonException) { state=new(Guid.NewGuid().ToString("N"),0,[]); }
    }
    public CacheJournalState Snapshot() { lock(gate) return state with { Changes=state.Changes.ToList() }; }
    public void Append(IEnumerable<CacheChange> changes) {
        lock(gate) {
            var next=state.Changes.ToList(); long head=state.Head;
            foreach(var change in changes) next.Add(change with { Sequence=++head });
            if(head==state.Head) return;
            if(next.Count>capacity) next.RemoveRange(0,next.Count-capacity);
            var replacement=new CacheJournalState(state.Epoch,head,next);
            using(var stream=new FileStream(path+".tmp",FileMode.Create,FileAccess.Write,FileShare.None)) { JsonSerializer.Serialize(stream,replacement); stream.Flush(true); }
            File.Move(path+".tmp",path,true); state=replacement; var previous=signal; signal=NewSignal(); previous.TrySetResult();
        }
    }
    public async Task Wait(long after,int seconds,CancellationToken cancellation) {
        Task pending; lock(gate) { if(state.Head>after) return; pending=signal.Task; }
        await Task.WhenAny(pending,Task.Delay(TimeSpan.FromSeconds(Math.Clamp(seconds,0,20)),cancellation)).ConfigureAwait(false); cancellation.ThrowIfCancellationRequested();
    }
    public static bool Expired(CacheJournalState state,string epoch,long after)=>epoch!=state.Epoch||after<0||after>state.Head||(state.Changes.Count>0&&after<state.Changes[0].Sequence-1);
}
