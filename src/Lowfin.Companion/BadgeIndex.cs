using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lowfin.Companion;

public sealed record BadgeIndexItem(Guid Id,int Mask,bool Container,Guid[] Parents,string[]? Labels=null,string? Revision=null,string? Access=null);
public sealed record BadgeIndexUser(Guid Id,string Scope);
public sealed record UserBadgeIndex(string Scope,Dictionary<Guid,int> Containers,Dictionary<Guid,string[]>? Tags=null,string? Visibility=null);
public sealed record BadgeSummary(int Mask,string[] Tags);
public sealed record BadgeIndexState(int Schema,DateTimeOffset FullCheckUtc,DateTimeOffset UpdatedUtc,Dictionary<Guid,BadgeIndexItem> Items,Dictionary<Guid,UserBadgeIndex> Users);
public interface IBadgeIndexSource {
    int? TotalCount()=>null;
    IEnumerable<BadgeIndexItem> All(CancellationToken cancellation);
    IEnumerable<BadgeIndexItem> All(IReadOnlyDictionary<Guid,BadgeIndexItem> previous,CancellationToken cancellation)=>All(cancellation);
    BadgeIndexItem? Read(Guid id);
    IReadOnlyList<BadgeIndexUser> Users();
    int Container(Guid item,Guid user);
    string[] ContainerTags(Guid item,Guid user,IReadOnlyDictionary<Guid,BadgeIndexItem> items,CancellationToken cancellation=default)=>[];
    string? Visibility(Guid user,CancellationToken cancellation)=>null;
    IReadOnlyDictionary<Guid,BadgeSummary> Summaries(IReadOnlyCollection<Guid> containers,Guid user,IReadOnlyDictionary<Guid,BadgeIndexItem> items,CancellationToken cancellation) {
        var result=new Dictionary<Guid,BadgeSummary>();foreach(Guid id in containers) { cancellation.ThrowIfCancellationRequested();result[id]=new(Container(id,user),ContainerTags(id,user,items,cancellation)); }return result;
    }
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
    private int reusedMedia,reusedSummaries,recalculatedSummaries;
    private readonly SemaphoreSlim writer=new(1);
    private readonly ConcurrentDictionary<Guid,long> dirty=new();
    private readonly ConcurrentDictionary<Guid,byte> dirtyUsers=new();
    private long eventSequence;
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
    public object Status()=>new { ready=Ready,building,phase,progress=Volatile.Read(ref percentage),scanned,completedContainers,reusedMedia,reusedSummaries,recalculatedSummaries,lastFullCheckUtc=state?.FullCheckUtc,lastUpdatedUtc=state?.UpdatedUtc,nextCheckUtc=state?.FullCheckUtc.AddHours(24),items=state?.Items.Count??0,users=state?.Users.Count??0,pending=dirty.Count+dirtyUsers.Count };
    private static bool SameFacts(BadgeIndexItem a,BadgeIndexItem b)=>a.Mask==b.Mask&&a.Container==b.Container&&a.Access==b.Access&&a.Parents.SequenceEqual(b.Parents)&&(a.Labels==null?b.Labels==null:b.Labels!=null&&a.Labels.SequenceEqual(b.Labels));
    private static HashSet<Guid> Affected(IReadOnlyDictionary<Guid,BadgeIndexItem> before,IReadOnlyDictionary<Guid,BadgeIndexItem> after) {
        var result=new HashSet<Guid>();var changedContainers=new HashSet<Guid>();
        void Add(BadgeIndexItem item) {result.UnionWith(item.Parents);if(item.Container) {result.Add(item.Id);changedContainers.Add(item.Id);}}
        foreach(var item in after.Values) if(!before.TryGetValue(item.Id,out var old)) Add(item);else if(!SameFacts(old,item)) {Add(old);Add(item);}
        foreach(var old in before.Values) if(!after.ContainsKey(old.Id)) Add(old);
        // A series access/metadata change also affects its season summaries.
        foreach(var item in after.Values) if(item.Container&&item.Parents.Any(changedContainers.Contains)) result.Add(item.Id);
        return result;
    }
    public void Queue(Guid id) { if(id==Guid.Empty) return;if(dirty.Count>=65536&&!dirty.ContainsKey(id)) RequestFull();else dirty[id]=Interlocked.Increment(ref eventSequence); }
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
    public string[]? GetLabels(Guid id,Guid user,string scope,bool container) {
        var snapshot=state;if(snapshot==null||!snapshot.Items.TryGetValue(id,out var item)) {Queue(id);return null;}
        if(!container) {if(item.Labels==null) Queue(id);return item.Labels;}
        if(!snapshot.Users.TryGetValue(user,out var entry)||entry.Scope!=scope) {QueueUser(user);return null;}
        if(entry.Tags!=null&&entry.Tags.TryGetValue(id,out var labels)) return labels;Queue(id);return null;
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
            building=true;phase="media";scanned=0;completedContainers=0;reusedMedia=reusedSummaries=recalculatedSummaries=0;bool force=Interlocked.Exchange(ref fullRequested,0)!=0;var before=state;var observed=dirty.ToArray();
            void Report(double value) { Volatile.Write(ref percentage,value);progress?.Report(value); }
            log.LogInformation("Lowfin cover index: rebuild started; previous snapshot remains available.");Report(1);
            using var heartbeat=new Timer(_=>log.LogInformation("Lowfin cover index: phase {Phase}, {Progress:F1}%, scanned {Scanned}, summaries {Completed}, elapsed {Seconds:F0}s.",phase,Volatile.Read(ref percentage),scanned,completedContainers,elapsed.Elapsed.TotalSeconds),null,TimeSpan.FromSeconds(15),TimeSpan.FromSeconds(15));
            var items=new Dictionary<Guid,BadgeIndexItem>();
            int? total=source.TotalCount();
            foreach(var item in source.All(force?new Dictionary<Guid,BadgeIndexItem>():before?.Items??new Dictionary<Guid,BadgeIndexItem>(),cancellation)) {
                cancellation.ThrowIfCancellationRequested();items[item.Id]=item;scanned++;if(before!=null&&before.Items.TryGetValue(item.Id,out var cached)&&ReferenceEquals(cached,item)) reusedMedia++;
                if(scanned%200==0) {
                    Report(total>0?Math.Min(55,1+54d*scanned/total.Value):Math.Min(50,1+49d*scanned/(scanned+2000d)));
                }
            }
            Report(55);phase="majorities";
            var accounts=source.Users();var containerIds=items.Values.Where(x=>x.Container).Select(x=>x.Id).ToArray();
            var affected=before==null?containerIds.ToHashSet():Affected(before.Items,items);
            long work=(long)accounts.Count*containerIds.Length;
            log.LogInformation("Lowfin cover index: {Items} media checked ({Reused} reused); checking {Containers} summaries for {Users} users.",items.Count,reusedMedia,containerIds.Length,accounts.Count);
            var users=new Dictionary<Guid,UserBadgeIndex>();
            foreach(var user in accounts) {
                cancellation.ThrowIfCancellationRequested();
                string? visibility=source.Visibility(user.Id,cancellation);
                UserBadgeIndex? previous=before?.Users.GetValueOrDefault(user.Id);
                bool refresh=force||previous==null||previous.Scope!=user.Scope||previous.Visibility!=visibility;
                var wanted=containerIds.Where(id=>refresh||affected.Contains(id)||!previous!.Containers.ContainsKey(id)||previous.Tags==null||!previous.Tags.ContainsKey(id)).ToArray();
                var wantedSet=wanted.ToHashSet();
                var updated=source.Summaries(wanted,user.Id,items,cancellation);
                var containers=new Dictionary<Guid,int>();var tags=new Dictionary<Guid,string[]>();
                foreach(Guid id in containerIds) {
                    cancellation.ThrowIfCancellationRequested();
                    if(updated.TryGetValue(id,out var value)) {containers[id]=value.Mask;tags[id]=ExpandedBadges.Summarize(value.Tags,value.Mask);recalculatedSummaries++;}
                    else if(!wantedSet.Contains(id)&&previous!=null) {containers[id]=previous.Containers[id];tags[id]=previous.Tags![id];reusedSummaries++;}
                    else throw new InvalidDataException("Incomplete cover summary batch.");
                    completedContainers++;Report(55+44d*completedContainers/Math.Max(1,work));
                }
                users[user.Id]=new(user.Scope,containers,tags,visibility);
            }
            phase="saving";Report(99);
            cancellation.ThrowIfCancellationRequested();
            var next=new BadgeIndexState(1,clock.GetUtcNow(),clock.GetUtcNow(),items,users);
            Save(next);state=next;
            // Remove only events that predate this scan and were not replaced while it ran.
            foreach(var entry in observed) ((ICollection<KeyValuePair<Guid,long>>)dirty).Remove(entry);
            Notify(before,next);phase="idle";Report(100);
            log.LogInformation("Lowfin cover index: completed {Items} media; {ReusedMedia} facts reused, {ReusedSummaries} summaries reused, {Calculated} recalculated in {Seconds}s.",items.Count,reusedMedia,reusedSummaries,recalculatedSummaries,elapsed.Elapsed.TotalSeconds);
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
            var before=state!;var items=new Dictionary<Guid,BadgeIndexItem>(before.Items);var parents=new HashSet<Guid>();var changedContainers=new HashSet<Guid>();bool modified=false;
            foreach(var id in batch) {
                cancellation.ThrowIfCancellationRequested();
                items.TryGetValue(id,out var old);
                var item=source.Read(id);
                if(old==null&&item==null||old!=null&&item!=null&&SameFacts(old,item)&&old.Revision==item.Revision) continue;
                modified=true;
                if(item==null) items.Remove(id);
                else items[id]=item;
                if(old!=null&&item!=null&&SameFacts(old,item)) continue;
                if(old!=null) {parents.UnionWith(old.Parents);if(old.Container) {parents.Add(id);changedContainers.Add(id);}}
                if(item!=null) {parents.UnionWith(item.Parents);if(item.Container) {parents.Add(id);changedContainers.Add(id);}}
            }
            if(!modified&&requestedUsers.Length==0) return;
            foreach(var item in items.Values) if(item.Container&&item.Parents.Any(changedContainers.Contains)) parents.Add(item.Id);
            foreach(var id in parents) {
                // Do not silently consume a parent's newer access revision while its own event
                // is still queued: that event must be able to invalidate all affected seasons.
                if(items.TryGetValue(id,out var existing)&&existing.Container) continue;
                var parent=source.Read(id);if(parent?.Container==true) items[id]=parent;else items.Remove(id);
            }
            var users=new Dictionary<Guid,UserBadgeIndex>();
            foreach(var user in parents.Count==0&&requestedUsers.Length==0?Array.Empty<BadgeIndexUser>():source.Users()) {
                bool rebuild=!before.Users.TryGetValue(user.Id,out var previous)||previous.Scope!=user.Scope;
                var counts=rebuild?new Dictionary<Guid,int>():new Dictionary<Guid,int>(before.Users[user.Id].Containers);
                var tags=rebuild?new Dictionary<Guid,string[]>():new Dictionary<Guid,string[]>(previous!.Tags??new());
                var affected=(rebuild?items.Values.Where(x=>x.Container).Select(x=>x.Id):parents).ToArray();
                var present=affected.Where(id=>items.TryGetValue(id,out var item)&&item.Container).ToArray();
                var updated=source.Summaries(present,user.Id,items,cancellation);
                foreach(var id in affected) {
                    cancellation.ThrowIfCancellationRequested();
                    if(updated.TryGetValue(id,out var summary)) {counts[id]=summary.Mask;tags[id]=ExpandedBadges.Summarize(summary.Tags,summary.Mask);}
                    else {counts.Remove(id);tags.Remove(id);}
                }
                users[user.Id]=new(user.Scope,counts,tags,rebuild?null:previous?.Visibility);
            }
            var next=before with { UpdatedUtc=clock.GetUtcNow(),Items=items,Users=parents.Count==0&&requestedUsers.Length==0?before.Users:users };
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
        foreach(var item in next.Items.Values) if(!before.Items.TryGetValue(item.Id,out var old)||old.Mask!=item.Mask||old.Labels!=null&&!old.Labels.SequenceEqual(item.Labels??[])) ids.Add(item.Id);
        foreach(var user in next.Users) {
            if(!before.Users.TryGetValue(user.Key,out var old)) continue;
            foreach(var value in user.Value.Containers) if(!old.Containers.TryGetValue(value.Key,out var mask)||mask!=value.Value||old.Tags!=null&&old.Tags.TryGetValue(value.Key,out var oldTags)&&!oldTags.SequenceEqual(user.Value.Tags?.GetValueOrDefault(value.Key)??[])) ids.Add(value.Key);
        }
        // Removed IDs already trigger native deletion/reset events; never synthesize a DTO for deleted media.
        if(ids.Count>0) changed(ids);
    }
}
