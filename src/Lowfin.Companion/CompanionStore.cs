using System.Text.Json;
using System.Collections.Concurrent;

namespace Lowfin.Companion;

/// <summary>Small atomic per-user/media records, with bounded striped locks and no Jellyfin schema changes.</summary>
public sealed class CompanionStore(string directory) {
    private sealed record ResumeEntry(long Revision,long Sequence,long Position,long Runtime,bool Owned,DateTimeOffset Updated);
    private sealed class ResumeIndex { public readonly object Gate=new(); public bool Loaded; public readonly ConcurrentDictionary<Guid,ResumeEntry> Rows=new(); }
    private readonly ConcurrentDictionary<Guid,ResumeIndex> resumeIndexes=new();
    private void Index(ResumeIndex index,MediaRecord record) {
        var entry=new ResumeEntry(record.Revision,record.Lease?.Sequence??-1,record.ResumeTicks,record.RuntimeTicks,record.ResumeOwned,record.UpdatedAt);
        index.Rows.AddOrUpdate(record.ItemId,entry,(_,old)=>entry.Revision>old.Revision||entry.Revision==old.Revision&&entry.Sequence>=old.Sequence?entry:old);
    }
    public IEnumerable<Guid> ResumeCandidates(Guid user) {
        var index=resumeIndexes.GetOrAdd(user,_=>new ResumeIndex());
        lock(index.Gate) { if(!index.Loaded) {
            string folder=Path.Combine(directory,user.ToString("N"));
            if(Directory.Exists(folder)) foreach(string path in Directory.EnumerateFiles(folder,"*.json")) if(Guid.TryParse(Path.GetFileNameWithoutExtension(path),out var item)&&item!=Guid.Empty) Index(index,Read(user,item));
            index.Loaded=true;
        } }
        return index.Rows.Where(pair=>pair.Value.Owned&&pair.Value.Position>0&&(pair.Value.Runtime<=0||pair.Value.Position<pair.Value.Runtime-TimeSpan.TicksPerSecond)).OrderByDescending(pair=>pair.Value.Updated).Select(pair=>pair.Key).ToArray();
    }
    private readonly SemaphoreSlim[] gates=Enumerable.Range(0,64).Select(_=>new SemaphoreSlim(1,1)).ToArray();
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    public SemaphoreSlim Gate(Guid user,Guid item) => gates[(uint)HashCode.Combine(user,item)%64];
    private string PathFor(Guid user,Guid item) => Path.Combine(directory,user.ToString("N"),item.ToString("N")+".json");
    public MediaRecord Read(Guid user,Guid item) {
        string path=PathFor(user,item); if(!File.Exists(path)) return new() { UserId=user,ItemId=item };
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete);
        var record=JsonSerializer.Deserialize<MediaRecord>(stream,Json) ?? throw new IOException("Invalid Companion record.");
        if(record.FormatVersion!=1 || record.UserId!=user || record.ItemId!=item || record.ResumeTicks<0 || record.RuntimeTicks<0) throw new IOException("Incompatible Companion record.");
        return record;
    }
    public void Save(MediaRecord record) {
        string path=PathFor(record.UserId,record.ItemId); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try {
            using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)) { JsonSerializer.Serialize(stream,record,Json); stream.Flush(true); }
            File.Move(temp,path,true);
            if(record.ItemId!=Guid.Empty&&resumeIndexes.TryGetValue(record.UserId,out var index)) Index(index,record);
        } finally { if(File.Exists(temp)) File.Delete(temp); }
    }
    public async Task<T> Locked<T>(Guid user,Guid item,Func<MediaRecord,Task<T>> action) {
        var gate=Gate(user,item); await gate.WaitAsync().ConfigureAwait(false);
        try { return await action(Read(user,item)).ConfigureAwait(false); } finally { gate.Release(); }
    }
}
