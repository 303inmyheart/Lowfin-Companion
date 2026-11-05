using System.Security.Cryptography;
using System.Text.Json;

namespace Lowfin.Companion;

public sealed record CacheChange(long Sequence,Guid ItemId,bool Removed,Guid[] Libraries,bool Reset=false);
public sealed record CacheJournalState(string Epoch,long Head,List<CacheChange> Changes,long FirstSequence=0);

/// <summary>Durable bounded history: append small checked records, checkpoint occasionally, page reads.</summary>
public sealed class CacheJournal {
    public const int DefaultCapacity=250000;
    private const int CheckpointEvery=8192,RecordEntries=32768,MaxRecordBytes=16*1024*1024;
    private readonly object gate=new();
    private readonly string path,logPath;
    private readonly int capacity;
    private CacheJournalState state;
    private long uncheckpointed;
    private bool faulted;
    private TaskCompletionSource signal=NewSignal();
    private static TaskCompletionSource NewSignal()=>new(TaskCreationOptions.RunContinuationsAsynchronously);
    public CacheJournal(string directory,int capacity=DefaultCapacity) {
        if(capacity<1)throw new ArgumentOutOfRangeException(nameof(capacity));
        Directory.CreateDirectory(directory);path=Path.Combine(directory,"cache-journal.json");logPath=Path.Combine(directory,"cache-journal.wal");this.capacity=capacity;
        try {
            state=JsonSerializer.Deserialize<CacheJournalState>(File.ReadAllText(path))??throw new IOException("Missing journal state.");
            Validate(state);Trim(state.Changes);Replay();
        } catch(Exception e) when(e is IOException or JsonException) {
            state=new(Guid.NewGuid().ToString("N"),0,[]);SaveSnapshot();TruncateLog();
        }
    }
    private static void Validate(CacheJournalState value) {
        if(string.IsNullOrEmpty(value.Epoch)||value.Head<0||value.Changes==null)throw new IOException("Invalid journal state.");
        long previous=value.Changes.Count==0?value.Head:value.Changes[0].Sequence-1;
        foreach(var change in value.Changes)if(change.Sequence!=++previous)throw new IOException("Unordered journal state.");
        if((value.Changes.Count>0&&value.Changes[0].Sequence<=0)||previous!=value.Head)throw new IOException("Invalid journal head.");
    }
    private void Trim(List<CacheChange> changes) {if(changes.Count>capacity)changes.RemoveRange(0,changes.Count-capacity);}
    public CacheJournalState Snapshot() {lock(gate)return state with { Changes=state.Changes.ToList() };}
    /// <summary>Copy only the requested page, retaining the real oldest sequence for gap detection.</summary>
    public CacheJournalState Snapshot(long after,int limit) {
        if(limit<1||limit>20)throw new ArgumentOutOfRangeException(nameof(limit));
        lock(gate) {
            long first=state.Changes.Count==0?state.Head+1:state.Changes[0].Sequence;
            int offset=(int)Math.Clamp(after<first?0:after-first+1,0,state.Changes.Count);
            return state with { FirstSequence=first,Changes=state.Changes.GetRange(offset,Math.Min(limit,state.Changes.Count-offset)) };
        }
    }
    public void Append(IEnumerable<CacheChange> changes) {
        // The feed ticks even while idle; never clone the retained history for an empty batch.
        var incoming=changes.ToList();if(incoming.Count==0)return;
        lock(gate) {
            if(faulted)throw new IOException("Journal requires recovery.");
            long head=state.Head;var added=incoming.Select(change=>change with { Sequence=++head }).ToList();
            var next=state.Changes.ToList();next.AddRange(added);Trim(next);
            var replacement=new CacheJournalState(state.Epoch,head,next);
            WriteLog(added);state=replacement;uncheckpointed+=added.Count;
            // The WAL is already durable. A failed optional checkpoint must not retract acknowledged entries.
            if(uncheckpointed>=Math.Min(capacity,CheckpointEvery))try {SaveSnapshot();TruncateLog();uncheckpointed=0;}catch(Exception error) when(error is IOException or UnauthorizedAccessException) { }
            var previous=signal;signal=NewSignal();previous.TrySetResult();
        }
    }
    private void WriteLog(List<CacheChange> added) {
        using var stream=new FileStream(logPath,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.Read);
        long start=stream.Length;stream.Position=start;
        try {
            using var writer=new BinaryWriter(stream,System.Text.Encoding.UTF8,true);
            foreach(var block in added.Chunk(RecordEntries)) {
                byte[] payload=JsonSerializer.SerializeToUtf8Bytes(new CacheJournalState(state.Epoch,block[^1].Sequence,block.ToList()));
                if(payload.Length>MaxRecordBytes)throw new IOException("Oversized journal record.");
                writer.Write(payload.Length);writer.Write(payload);writer.Write(SHA256.HashData(payload));
            }
            writer.Flush();stream.Flush(true);
        } catch {
            try {stream.SetLength(start);stream.Flush(true);}catch {faulted=true;}
            throw;
        }
    }
    private void Replay() {
        if(!File.Exists(logPath))return;
        using var stream=new FileStream(logPath,FileMode.Open,FileAccess.ReadWrite,FileShare.Read);
        using var reader=new BinaryReader(stream,System.Text.Encoding.UTF8,true);
        while(stream.Position<stream.Length) {
            long start=stream.Position;
            if(stream.Length-start<sizeof(int)) {stream.SetLength(start);stream.Flush(true);break;}
            int size=reader.ReadInt32();if(size<=0||size>MaxRecordBytes)throw new IOException("Invalid journal record size.");
            if(stream.Length-stream.Position<size+32L) {stream.SetLength(start);stream.Flush(true);break;}
            byte[] payload=reader.ReadBytes(size),hash=reader.ReadBytes(32);
            if(!CryptographicOperations.FixedTimeEquals(hash,SHA256.HashData(payload)))throw new IOException("Invalid journal record checksum.");
            var batch=JsonSerializer.Deserialize<CacheJournalState>(payload)??throw new IOException("Missing journal record.");Validate(batch);
            if(batch.Epoch!=state.Epoch)throw new IOException("Journal epoch mismatch.");
            // A checkpoint may have reached disk before an interrupted WAL truncation.
            var fresh=batch.Changes.Where(change=>change.Sequence>state.Head).ToList();if(fresh.Count==0)continue;
            if(fresh[0].Sequence!=state.Head+1)throw new IOException("Journal sequence gap.");
            state.Changes.AddRange(fresh);Trim(state.Changes);state=state with {Head=batch.Head};uncheckpointed+=fresh.Count;
        }
    }
    private void SaveSnapshot() {
        using(var stream=new FileStream(path+".tmp",FileMode.Create,FileAccess.Write,FileShare.None)) {JsonSerializer.Serialize(stream,state);stream.Flush(true);}
        File.Move(path+".tmp",path,true);
    }
    private void TruncateLog() {using var stream=new FileStream(logPath,FileMode.Create,FileAccess.Write,FileShare.Read);stream.Flush(true);}
    public async Task Wait(long after,int seconds,CancellationToken cancellation) {
        Task pending;lock(gate) {if(state.Head>after)return;pending=signal.Task;}
        await Task.WhenAny(pending,Task.Delay(TimeSpan.FromSeconds(Math.Clamp(seconds,0,20)),cancellation)).ConfigureAwait(false);cancellation.ThrowIfCancellationRequested();
    }
    public static bool Expired(CacheJournalState state,string epoch,long after) {
        long first=state.FirstSequence>0?state.FirstSequence:state.Changes.Count>0?state.Changes[0].Sequence:state.Head+1;
        return epoch!=state.Epoch||after<0||after>state.Head||after<first-1;
    }
}
