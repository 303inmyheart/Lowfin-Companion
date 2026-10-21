using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lowfin.Companion;

public sealed record BadgeIndexItem(Guid Id,int Mask,bool Container,Guid[] Parents);
public sealed record BadgeIndexUser(Guid Id,string Scope);
public sealed record UserBadgeIndex(string Scope,Dictionary<Guid,int> Containers);
public sealed record BadgeIndexState(int Schema,DateTimeOffset FullCheckUtc,DateTimeOffset UpdatedUtc,Dictionary<Guid,BadgeIndexItem> Items,Dictionary<Guid,UserBadgeIndex> Users);
public interface IBadgeIndexSource {
    int? TotalCount()=>null;
    IEnumerable<BadgeIndexItem> All(CancellationToken cancellation);
    BadgeIndexItem? Read(Guid id);
    IReadOnlyList<BadgeIndexUser> Users();
    int Container(Guid item,Guid user);
}

/// <summary>Immutable published snapshots. Only the background worker/job performs library queries.</summary>
public sealed class BadgeIndex {
    private readonly string file;
    private readonly IBadgeIndexSource source;
    private readonly Action<IReadOnlyCollection<Guid>> changed;
    private readonly TimeProvider clock;
    private readonly ILogger<BadgeIndex> log;
    private double percentage;
    private string phase="idle";
    private int scanned,completedContainers;
    private readonly SemaphoreSlim writer=new(1);
    private readonly ConcurrentDictionary<Guid,byte> dirty=new(),dirtyUsers=new();
    private volatile BadgeIndexState? state;
    private DateTimeOffset persistedUtc;
    private volatile bool building;
    private int fullRequested;
    public BadgeIndex(string directory,IBadgeIndexSource source,Action<IReadOnlyCollection<Guid>> changed,TimeProvider? clock=null,ILogger<BadgeIndex>? log=null) {
        this.log=log??NullLogger<BadgeIndex>.Instance;this.source=source;this.changed=changed;this.clock=clock??TimeProvider.System;Directory.CreateDirectory(directory);file=Path.Combine(directory,"badge-index.json");
        try { var saved=JsonSerializer.Deserialize<BadgeIndexState>(File.ReadAllText(file));if(saved?.Schema==1&&saved.Items!=null&&saved.Users!=null) state=saved; }
        catch(Exception error) when(error is IOException or JsonException) { }
    }
    public bool Ready=>state!=null;
    public bool FullRequested=>Volatile.Read(ref fullRequested)!=0;
    public bool Due=>state==null||clock.GetUtcNow()-state.FullCheckUtc>=TimeSpan.FromHours(24);
    public object Status()=>new { ready=Ready,building,phase,progress=Volatile.Read(ref percentage),scanned,completedContainers,lastFullCheckUtc=state?.FullCheckUtc,lastUpdatedUtc=state?.UpdatedUtc,nextCheckUtc=state?.FullCheckUtc.AddHours(24),items=state?.Items.Count??0,users=state?.Users.Count??0,pending=dirty.Count+dirtyUsers.Count };
    public void Queue(Guid id) { if(id==Guid.Empty) return;if(dirty.Count>=4096) RequestFull();else dirty[id]=0; }
    public void QueueUser(Guid id) { if(id!=Guid.Empty) dirtyUsers[id]=0; }
    public void RequestFull()=>Interlocked.Exchange(ref fullRequested,1);
    public int? Get(Guid id,Guid user,string scope,bool container) {
        var snapshot=state;
        if(snapshot==null||!snapshot.Items.TryGetValue(id,out var item)) { Queue(id);return null; }
        if(!container) return item.Mask;
        if(!snapshot.Users.TryGetValue(user,out var entry)||entry.Scope!=scope) { dirtyUsers[user]=0;return null; }
        if(entry.Containers.TryGetValue(id,out var mask)) return mask;
        Queue(id);return null;
    }
    public async Task Rebuild(CancellationToken cancellation,IProgress<double>? progress=null) {
        var requestedAt=state;
        bool waiting=false;
        while(!await writer.WaitAsync(TimeSpan.FromSeconds(1),cancellation)) {
            if(!waiting) { log.LogInformation("Lowfin cover index: waiting for the active index writer.");waiting=true; }
            progress?.Report(Volatile.Read(ref percentage));
        }
        var elapsed=System.Diagnostics.Stopwatch.StartNew();
        try {
            if(state!=requestedAt&&state!=null&&(requestedAt==null||state.FullCheckUtc>requestedAt.FullCheckUtc)) { progress?.Report(100);return; }
            building=true;phase="media";scanned=0;completedContainers=0;Interlocked.Exchange(ref fullRequested,0);var before=state;
            void Report(double value) { Volatile.Write(ref percentage,value);progress?.Report(value); }
            log.LogInformation("Lowfin cover index: rebuild started; previous snapshot remains available.");Report(1);
            using var heartbeat=new Timer(_=>log.LogInformation("Lowfin cover index: phase {Phase}, {Progress:F1}%, scanned {Scanned}, summaries {Completed}, elapsed {Seconds:F0}s.",phase,Volatile.Read(ref percentage),scanned,completedContainers,elapsed.Elapsed.TotalSeconds),null,TimeSpan.FromSeconds(15),TimeSpan.FromSeconds(15));
            var items=new Dictionary<Guid,BadgeIndexItem>();
            int? total=source.TotalCount();
            foreach(var item in source.All(cancellation)) {
                cancellation.ThrowIfCancellationRequested();items[item.Id]=item;scanned++;
                if(scanned%200==0) {
                    Report(total>0?Math.Min(55,1+54d*scanned/total.Value):Math.Min(50,1+49d*scanned/(scanned+2000d)));
                }
            }
            Report(55);phase="majorities";
            var accounts=source.Users();var containerItems=items.Values.Where(x=>x.Container).ToArray();
            long work=(long)accounts.Count*containerItems.Length;
            log.LogInformation("Lowfin cover index: {Items} media scanned; computing {Containers} series/season summaries for {Users} users.",items.Count,containerItems.Length,accounts.Count);
            var users=new Dictionary<Guid,UserBadgeIndex>();
            foreach(var user in accounts) {
                var containers=new Dictionary<Guid,int>();
                foreach(var item in containerItems) {
                    cancellation.ThrowIfCancellationRequested();containers[item.Id]=source.Container(item.Id,user.Id);completedContainers++;
                    Report(55+44d*completedContainers/Math.Max(1,work));
                }
                users[user.Id]=new(user.Scope,containers);
            }
            phase="saving";Report(99);
            cancellation.ThrowIfCancellationRequested();
            var next=new BadgeIndexState(1,clock.GetUtcNow(),clock.GetUtcNow(),items,users);
            Save(next);state=next;Notify(before,next);phase="idle";Report(100);
            log.LogInformation("Lowfin cover index: completed {Items} media and {Summaries} summaries in {Seconds}s.",items.Count,completedContainers,elapsed.Elapsed.TotalSeconds);
        } catch(OperationCanceledException) {
            phase="cancelled";RequestFull();log.LogInformation("Lowfin cover index: cancelled after {Seconds}s; previous snapshot retained.",elapsed.Elapsed.TotalSeconds);throw;
        } catch(Exception error) {
            phase="failed";RequestFull();log.LogError(error,"Lowfin cover index: rebuild failed after {Seconds}s; previous snapshot retained.",elapsed.Elapsed.TotalSeconds);throw;
        } finally { building=false;writer.Release(); }
    }
    public async Task Update(CancellationToken cancellation) {
        if(state==null||dirty.IsEmpty&&dirtyUsers.IsEmpty) return;
        await writer.WaitAsync(cancellation);
        var batch=dirty.Keys.Take(128).ToArray();var requestedUsers=dirtyUsers.Keys.ToArray();
        foreach(var id in batch) dirty.TryRemove(id,out _);foreach(var id in requestedUsers) dirtyUsers.TryRemove(id,out _);
        try {
            var before=state!;var items=new Dictionary<Guid,BadgeIndexItem>(before.Items);var parents=new HashSet<Guid>();
            foreach(var id in batch) {
                cancellation.ThrowIfCancellationRequested();
                if(items.TryGetValue(id,out var old)) { parents.UnionWith(old.Parents);if(old.Container) parents.Add(id); }
                var item=source.Read(id);
                if(item==null) items.Remove(id);
                else { items[id]=item;parents.UnionWith(item.Parents);if(item.Container) parents.Add(id); }
            }
            foreach(var id in parents) { var parent=source.Read(id);if(parent?.Container==true) items[id]=parent;else items.Remove(id); }
            var users=new Dictionary<Guid,UserBadgeIndex>();
            foreach(var user in source.Users()) {
                bool rebuild=!before.Users.TryGetValue(user.Id,out var previous)||previous.Scope!=user.Scope;
                var counts=rebuild?new Dictionary<Guid,int>():new Dictionary<Guid,int>(before.Users[user.Id].Containers);
                var affected=rebuild?items.Values.Where(x=>x.Container).Select(x=>x.Id):parents;
                foreach(var id in affected) { cancellation.ThrowIfCancellationRequested();if(items.TryGetValue(id,out var item)&&item.Container) counts[id]=source.Container(id,user.Id);else counts.Remove(id); }
                users[user.Id]=new(user.Scope,counts);
            }
            var next=before with { UpdatedUtc=clock.GetUtcNow(),Items=items,Users=users };
            state=next;if(clock.GetUtcNow()-persistedUtc>=TimeSpan.FromSeconds(30)) Save(next);Notify(before,next);
        } catch { foreach(var id in batch) Queue(id);foreach(var id in requestedUsers) dirtyUsers[id]=0;throw; }
        finally { writer.Release(); }
    }
    public async Task Flush(CancellationToken cancellation) { await writer.WaitAsync(cancellation);try { if(state!=null) Save(state); }finally {writer.Release();} }
    private void Save(BadgeIndexState snapshot) {
        string temporary=file+".tmp";
        try { using(var stream=new FileStream(temporary,FileMode.Create,FileAccess.Write,FileShare.None)) { JsonSerializer.Serialize(stream,snapshot);stream.Flush(true); }File.Move(temporary,file,true);persistedUtc=clock.GetUtcNow(); }
        finally { File.Delete(temporary); }
    }
    private void Notify(BadgeIndexState? before,BadgeIndexState next) {
        if(before==null) return;
        var ids=new HashSet<Guid>();
        foreach(var item in next.Items.Values) if(!before.Items.TryGetValue(item.Id,out var old)||old.Mask!=item.Mask) ids.Add(item.Id);
        foreach(var user in next.Users) {
            if(!before.Users.TryGetValue(user.Key,out var old)) continue;
            foreach(var value in user.Value.Containers) if(!old.Containers.TryGetValue(value.Key,out var mask)||mask!=value.Value) ids.Add(value.Key);
        }
        // Removed IDs already trigger native deletion/reset events; never synthesize a DTO for deleted media.
        if(ids.Count>0) changed(ids);
    }
}
