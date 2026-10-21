using MediaBrowser.Controller.Entities;
using Jellyfin.Data.Events;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lowfin.Companion;

public sealed class BadgeIndexWorker(BadgeIndex index,ILibraryManager library,IUserManager users,ILogger<BadgeIndexWorker> log) : BackgroundService {
    private void Changed(object? sender,ItemChangeEventArgs args) {
        if(args.Item is CollectionFolder) index.RequestFull();else if(BadgeIndexSource.Supported(args.Item)) index.Queue(args.Item.Id);
    }
    private void UserChanged(object? sender,GenericEventArgs<User> args)=>index.QueueUser(args.Argument.Id);
    public override Task StartAsync(CancellationToken token) {
        users.OnUserUpdated+=UserChanged;
        library.ItemAdded+=Changed;library.ItemUpdated+=Changed;library.ItemRemoved+=Changed;
        return base.StartAsync(token);
    }
    protected override async Task ExecuteAsync(CancellationToken cancellation) {
        await Task.Yield(); // Never hold server startup for the first index.
        bool initial=true;
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(2));
        try { do {
            if(Plugin.Instance?.Configuration.ImageBadgesEnabled==true) {
                try {
                    // After server downtime, reconcile in the background while the persisted index remains readable.
                    if(initial||!index.Ready||index.FullRequested||index.Due) { await index.Rebuild(cancellation);initial=false; }
                    await index.Update(cancellation);
                } catch(OperationCanceledException) when(cancellation.IsCancellationRequested) { throw; }
                catch(Exception error) { log.LogWarning(error,"Companion tag index update postponed; previous snapshot retained.");await Task.Delay(TimeSpan.FromSeconds(15),cancellation); }
            }
        } while(await timer.WaitForNextTickAsync(cancellation)); }
        catch(OperationCanceledException) when(cancellation.IsCancellationRequested) { }
    }
    public override async Task StopAsync(CancellationToken token) {
        users.OnUserUpdated-=UserChanged;
        library.ItemAdded-=Changed;library.ItemUpdated-=Changed;library.ItemRemoved-=Changed;
        await base.StopAsync(token);await index.Flush(token);
    }
}

public sealed class BadgeIndexTask(BadgeIndex index) : IScheduledTask {
    public string Name=>"Lowfin · Update cover tag index";
    public string Key=>"LowfinCompanionBadgeIndex";
    public string Description=>"Reconcile DVD/5.1/HDR facts and user-scoped series/season majorities. Episode ratings remain uncached.";
    public string Category=>"Lowfin Companion";
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()=>[new() { Type=TaskTriggerInfoType.IntervalTrigger,IntervalTicks=TimeSpan.FromHours(24).Ticks }];
    public async Task ExecuteAsync(IProgress<double> progress,CancellationToken cancellationToken) {
        if(Plugin.Instance?.Configuration.ImageBadgesEnabled!=true) return;
        progress.Report(0);await index.Rebuild(cancellationToken,progress);progress.Report(100);
    }
}
