using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lowfin.Companion;

/// <summary>Jellyfin callbacks only enqueue IDs. Disk writes and DTO work never run in their handlers.</summary>
public sealed class CacheFeed(CacheJournal journal,ILibraryManager library,ILogger<CacheFeed> log) : BackgroundService {
    private sealed record Pending(CacheChange Change,long Revision,bool Forced);
    private readonly ConcurrentDictionary<Guid,Pending> pending=new();
    private readonly ConcurrentDictionary<Guid,long> delivered=new();
    private int reset;
    private volatile bool healthy=true;
    public CacheJournal Journal=>healthy?journal:throw new IOException("Cache journal unavailable");
    public static bool Supported(BaseItem item)=>item is Movie or Series or Season or Episode or BoxSet || item.GetType().Name=="Video";
    private void Changed(object? sender,ItemChangeEventArgs args)=>Queue(args,false);
    private void Removed(object? sender,ItemChangeEventArgs args)=>Queue(args,true);
    private void Queue(ItemChangeEventArgs args,bool removed) {
        if(args.Item is CollectionFolder) { Interlocked.Exchange(ref reset,1); return; }
        if(!Supported(args.Item)) return;
        // Moves and removals need an authoritative reconciliation: event args do not expose the previous visibility.
        // Ordinary metadata edits are the common cheap incremental path.
        if(removed) { delivered.TryRemove(args.Item.Id,out _);Interlocked.Exchange(ref reset,1); return; }
        long revision=args.Item.DateLastSaved.Ticks;
        bool forced=args.UpdateReason>=ItemUpdateType.ImageUpdate;
        if(!forced&&revision>0&&delivered.TryGetValue(args.Item.Id,out long sent)&&sent==revision) return;
        if(pending.Count>=32768) { Interlocked.Exchange(ref reset,1); return; }
        Enqueue(args.Item.Id,revision,forced);
    }
    private void Enqueue(Guid id,long revision,bool forced)=>pending.AddOrUpdate(id,new Pending(new(0,id,false,[]),revision,forced),(_,previous)=>new(new(0,id,false,[]),revision>0?revision:previous.Revision,forced||previous.Forced));
    public void NotifyBadgeChanges(IReadOnlyCollection<Guid> ids) {
        foreach(var id in ids) {
            if(pending.Count>=32768) { Interlocked.Exchange(ref reset,1);break; }
            Enqueue(id,0,true); // A computed tag change does not change Jellyfin's item ETag.
        }
    }
    public override async Task StartAsync(CancellationToken token) {
        library.ItemAdded+=Changed; library.ItemUpdated+=Changed; library.ItemRemoved+=Removed;
        // No supported event stream exists while Jellyfin is stopped. Never pretend that this gap was observed.
        journal.Append([new(0,Guid.Empty,false,[],true)]);
        await base.StartAsync(token).ConfigureAwait(false);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        using var timer=new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try { while(await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) {
            var batch=new List<CacheChange>();var accepted=new List<KeyValuePair<Guid,Pending>>();
            if(Interlocked.Exchange(ref reset,0)!=0) batch.Add(new(0,Guid.Empty,false,[],true));
            foreach(var id in pending.Keys) if(pending.TryRemove(id,out var change)) {
                if(!change.Forced&&change.Revision>0&&delivered.TryGetValue(id,out long sent)&&sent==change.Revision) continue;
                batch.Add(change.Change);accepted.Add(new(id,change));
            }
            try {
                journal.Append(batch);healthy=true;
                // Remember revisions only after durable append, never in the synchronous callback.
                if(delivered.Count>=65536) delivered.Clear();
                foreach(var pair in accepted) if(pair.Value.Revision>0) delivered[pair.Key]=pair.Value.Revision;
            }
            catch(Exception exception) { healthy=false; Interlocked.Exchange(ref reset,1); log.LogError(exception,"Could not persist cache changes; reconciliation will be requested."); }
        } } catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested) { }
    }
    public override async Task StopAsync(CancellationToken token) {
        library.ItemAdded-=Changed; library.ItemUpdated-=Changed; library.ItemRemoved-=Removed;
        await base.StopAsync(token).ConfigureAwait(false);
    }
    public string Scope(User user) {
        var roots=library.GetUserRootFolder().GetChildren(user,true).OfType<Folder>().Select(x=>x.Id.ToString("N")).Order().ToArray();
        var value=new { user.Id,user.MaxParentalRatingScore,user.MaxParentalRatingSubScore,roots,permissions=user.Permissions.OrderBy(p=>p.Kind).Select(p=>new { p.Kind,p.Value }),preferences=user.Preferences.OrderBy(p=>p.Kind).Select(p=>new { p.Kind,p.Value }) };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
    }
    public static string Cursor(CacheJournalState state,long after,string scope)=>Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new CacheCursor(state.Epoch,after,scope))));
    public static CacheCursor? Parse(string? cursor) {
        if(string.IsNullOrEmpty(cursor)||cursor.Length>1024) return null;
        try { return JsonSerializer.Deserialize<CacheCursor>(Convert.FromBase64String(cursor)); } catch(Exception e) when(e is FormatException or JsonException) { return null; }
    }
}
public sealed record CacheCursor(string Epoch,long After,string Scope);
