using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lowfin.Companion;

public sealed class AudioLoudnessWorker(AudioLoudnessIndex index,ILibraryManager library,ITaskManager tasks,ILogger<AudioLoudnessWorker> log) : BackgroundService {
    private int requested=1;
    private int wasEnabled;
    private readonly SemaphoreSlim wakeup=new(0,1);
    private void Signal() { try { wakeup.Release(); } catch(SemaphoreFullException) { } }
    private void Changed(object? sender,ItemChangeEventArgs args) {
        if(Plugin.Instance?.Configuration.AudioLoudnessEnabled!=true) return;
        // Library-folder bookkeeping must not restart a whole-library audio check. Actual
        // additions/updates/removals arrive as Video events; startup/daily reconciliation remain.
        if(args.Item is Video) index.Queue(args.Item.Id);else return;
        Interlocked.Exchange(ref requested,1);Signal();
    }
    private void SettingsChanged() {
        int enabled=Plugin.Instance?.Configuration.AudioLoudnessEnabled==true?1:0;
        if(Interlocked.Exchange(ref wasEnabled,enabled)==enabled) return;
        if(enabled==0) index.Interrupt();else { index.RequestFull();Interlocked.Exchange(ref requested,1); }
        Signal();
    }
    public override Task StartAsync(CancellationToken token) {
        wasEnabled=Plugin.Instance?.Configuration.AudioLoudnessEnabled==true?1:0;
        library.ItemAdded+=Changed;library.ItemUpdated+=Changed;library.ItemRemoved+=Changed;
        if(Plugin.Instance!=null) Plugin.Instance.SettingsChanged+=SettingsChanged;
        return base.StartAsync(token);
    }
    protected override async Task ExecuteAsync(CancellationToken cancellation) {
        await Task.Yield();
        try {
            do {
                if(Plugin.Instance?.Configuration.AudioLoudnessEnabled!=true) { await wakeup.WaitAsync(TimeSpan.FromSeconds(5),cancellation);continue; }
                // All execution goes through Jellyfin's real task manager: visible, cancellable,
                // manually startable, with the same single-consumer queue for library events.
                try {
                    var task=tasks.ScheduledTasks.FirstOrDefault(worker=>worker.ScheduledTask is AudioLoudnessTask);
                    if(task?.State==TaskState.Idle&&!index.Running&&Interlocked.Exchange(ref requested,0)!=0) tasks.QueueIfNotRunning<AudioLoudnessTask>();
                }
                catch(Exception error) when(error is not OperationCanceledException) { log.LogWarning("Lowfin loudness: background check postponed ({ErrorType}).",error.GetType().Name);await Task.Delay(TimeSpan.FromSeconds(30),cancellation); }
                await wakeup.WaitAsync(TimeSpan.FromSeconds(5),cancellation);
            } while(!cancellation.IsCancellationRequested);
        } catch(OperationCanceledException) when(cancellation.IsCancellationRequested) { }
    }
    public override async Task StopAsync(CancellationToken token) {
        library.ItemAdded-=Changed;library.ItemUpdated-=Changed;library.ItemRemoved-=Changed;
        if(Plugin.Instance!=null) Plugin.Instance.SettingsChanged-=SettingsChanged;
        await base.StopAsync(token);
    }
}

public sealed class AudioLoudnessTask(AudioLoudnessIndex index) : IScheduledTask {
    public string Name=>"Lowfin · Measure audio loudness";
    public string Key=>"LowfinCompanionAudioLoudness";
    public string Description=>"Measure new or changed local movie/episode audio tracks. Reuse persistent measurements for unchanged files. Live TV is excluded.";
    public string Category=>"Lowfin Companion";
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()=>[new() { Type=TaskTriggerInfoType.IntervalTrigger,IntervalTicks=TimeSpan.FromHours(24).Ticks }];
    public Task ExecuteAsync(IProgress<double> progress,CancellationToken cancellationToken) {
        if(Plugin.Instance?.Configuration.AudioLoudnessEnabled!=true) { progress.Report(100);return Task.CompletedTask; }
        return index.FullRequested||!index.HasPending||index.LastReconciled<DateTimeOffset.UtcNow.AddHours(-24)
            ?index.Reconcile(cancellationToken,progress):index.Update(cancellationToken,progress);
    }
}
