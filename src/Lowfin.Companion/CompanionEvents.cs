using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lowfin.Companion;

/// <summary>Only the actual gateway scope may correct playback saves; unrelated clients are never guessed from save reasons.</summary>
public sealed class CompanionEvents(CompanionService service,IUserDataManager data,IUserManager users,ISessionManager sessions,ActionOrder order,ILogger<CompanionEvents> log) : IHostedService {
    private readonly AsyncLocal<bool> manualGuard=new();
    public Task StartAsync(CancellationToken cancellationToken) {
        data.UserDataSaved+=Saved; sessions.PlaybackStart+=ForeignStart; sessions.PlaybackProgress+=Progress; sessions.PlaybackStopped+=Stopped;
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken cancellationToken) {
        data.UserDataSaved-=Saved; sessions.PlaybackStart-=ForeignStart; sessions.PlaybackProgress-=Progress; sessions.PlaybackStopped-=Stopped;
        return Task.CompletedTask;
    }
    private void Saved(object? sender,UserDataSaveEventArgs args) {
        if(manualGuard.Value) return;
        var scope=PlaybackScope.Current;
        if(scope!=null&&scope.Matches(args.UserId,args.Item)) { service.Correct(scope,args); return; }
        if(args.SaveReason is not (UserDataSaveReason.TogglePlayed or UserDataSaveReason.UpdateUserData)) return;
        long ordinal=order.Next(); var gate=service.Store.Gate(args.UserId,args.Item.Id); gate.Wait();
        try {
            var record=service.Store.Read(args.UserId,args.Item.Id);
            if(record.Rule==null&&!record.ResumeOwned&&record.Lease==null) return;
            // Favorites/rating-only DTO edits are not progress resets. Identical-value external resets cannot be distinguished by this API.
            if(args.SaveReason==UserDataSaveReason.UpdateUserData&&record.NativeItemId==args.Item.Id&&record.NativePlayed==args.UserData.Played&&record.NativePositionTicks==args.UserData.PlaybackPositionTicks) return;
            if(!order.Newer(record,ordinal)) return;
            order.Apply(record,ordinal); record.Revision++; record.ManualFence=true; record.ResumeOwned=true;
            record.Played=args.UserData.Played; record.ResumeTicks=args.SaveReason==UserDataSaveReason.TogglePlayed?0:Math.Max(0,args.UserData.PlaybackPositionTicks);
            record.NativeItemId=args.Item.Id; record.NativePlayed=args.UserData.Played; record.NativePositionTicks=record.ResumeTicks;
            record.UpdatedAt=DateTimeOffset.UtcNow; service.Store.Save(record);
            // The external core transaction committed before this callback waited for the actor. Restore it after an in-flight Lowfin save so the manual action wins.
            var user=users.GetUserById(args.UserId); if(user!=null) {
                args.UserData.PlaybackPositionTicks=record.ResumeTicks; manualGuard.Value=true;
                try { data.SaveUserData(user,args.Item,args.UserData,args.SaveReason,CancellationToken.None); }
                finally { manualGuard.Value=false; }
            }
        } catch(Exception exception) { log.LogError(exception,"Could not persist a Companion manual-action fence."); }
        finally { gate.Release(); }
    }
    private void ForeignStart(object? sender,PlaybackProgressEventArgs args) {
        if(PlaybackScope.Current!=null||!CompanionService.SupportedItem(args.Item)) return;
        foreach(var user in args.Users) {
            var gate=service.Store.Gate(user.Id,args.Item.Id); gate.Wait();
            try {
                var record=service.Store.Read(user.Id,args.Item.Id); if(record.Lease==null) continue;
                // Core dispatches session notifications asynchronously: our request scope may already be disposed.
                // The durable lease identifies this start without attributing unrelated user-data saves to it.
                if(record.Lease.DeviceId==args.DeviceId&&record.Lease.PlaySessionId==args.PlaySessionId) continue;
                record.Revision++; record.Lease=null; record.ResumeOwned=false; record.ResumeTicks=0; record.ManualFence=false;
                record.UpdatedAt=DateTimeOffset.UtcNow; service.Store.Save(record);
            } catch(Exception exception) { log.LogError(exception,"Could not retire a Companion lease after another client started."); }
            finally { gate.Release(); }
        }
    }
    private void Progress(object? sender,PlaybackProgressEventArgs args) { if(args.IsAutomated) ReconcileOwned(args,false); }
    private void Stopped(object? sender,PlaybackStopEventArgs args) => ReconcileOwned(args,true);
    private void ReconcileOwned(PlaybackProgressEventArgs args,bool stopped) {
        if(PlaybackScope.Current!=null||!CompanionService.SupportedItem(args.Item)) return;
        foreach(var user in args.Users) {
            var gate=service.Store.Gate(user.Id,args.Item.Id); gate.Wait();
            try {
                var record=service.Store.Read(user.Id,args.Item.Id); var lease=record.Lease;
                if(lease==null||lease.Stopped||lease.DeviceId!=args.DeviceId||lease.PlaySessionId!=args.PlaySessionId) continue;
                var state=service.State(record,user,args.Item);
                // An already queued automatic timer or server-side crash cleanup must not invent progress.
                if(state.EffectiveRule.Mode!=CompletionMode.Default||record.ManualFence) {
                    var native=data.GetUserData(user,args.Item) ?? throw new InvalidOperationException();
                    using var scope=new PlaybackScope { User=user,Item=args.Item,ProgressItem=args.Item,Record=record,Rule=state.EffectiveRule,Position=record.ResumeTicks };
                    service.Correct(scope,new UserDataSaveEventArgs { UserId=user.Id,Item=args.Item,UserData=native,SaveReason=UserDataSaveReason.PlaybackProgress });
                }
                if(stopped) lease.Stopped=true;
                record.UpdatedAt=DateTimeOffset.UtcNow; service.Store.Save(record);
            } catch(Exception exception) { log.LogError(exception,"Could not reconcile an owned Companion session."); }
            finally { gate.Release(); }
        }
    }
}
