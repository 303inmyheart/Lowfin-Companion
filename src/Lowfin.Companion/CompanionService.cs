using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Jellyfin.Data.Enums;

namespace Lowfin.Companion;

public sealed class CompanionService(CompanionStore store,IUserDataManager data,ISessionManager sessions,ITranscodeManager transcodes,ActionOrder order) {
    internal CompanionStore Store => store;
    internal IUserDataManager Data => data;
    public static bool SupportedItem(BaseItem item) => item is Movie or Episode;
    private CompletionRule Effective(MediaRecord record,long runtime,out bool inherited) {
        var global=store.Read(record.UserId,Guid.Empty).LongVideo;
        inherited=record.Rule is null && global?.Enabled==true && runtime>0 && runtime >= (long)global.MinimumSeconds*TimeSpan.TicksPerSecond;
        return record.Rule ?? (inherited?global!.Rule:new CompletionRule { PersistentResume=false });
    }
    private static bool Active(CompletionRule rule) => rule.Mode!=CompletionMode.Default||rule.PersistentResume;
    internal ItemState State(MediaRecord record,User user,BaseItem item) {
        long runtime=record.RuntimeTicks>0?record.RuntimeTicks:item.GetRunTimeTicksForPlayState();
        var rule=Effective(record,runtime,out bool inherited);
        var native=data.GetUserData(user,item) ?? throw new InvalidOperationException("Missing user data.");
        bool active=Active(rule);
        long resume=active&&rule.PersistentResume&&record.ResumeOwned?record.ResumeTicks:native.PlaybackPositionTicks;
        return new(record.Revision,record.Rule,rule,inherited,resume,runtime,resume>0&&(runtime<=0||resume<runtime-TimeSpan.TicksPerSecond),active&&(record.ManualFence || rule.Mode!=CompletionMode.Default&&(record.ResumeOwned||record.Lease!=null))?record.Played:native.Played,record.UpdatedAt,record.Lease?.Id,record.Lease?.Sequence??-1,record.ManualFence,active,ResumeVisibility.InHome(user,item));
    }
    public Task<ItemState> Get(User user,BaseItem item) => store.Locked(user.Id,item.Id,record=>Task.FromResult(State(record,user,item)));
    public async Task<object> Resume(User user,ILibraryManager library,int limit,IEnumerable<Guid>? knownItems=null,Guid? parentId=null) {
        if(limit<1||limit>100) throw new ArgumentException("Invalid limit.");
        var results=new List<object>();
        var candidates=store.ResumeCandidates(user.Id).ToArray(); var known=(knownItems??[]).Distinct().Take(100).ToArray();
        HashSet<Guid>? scoped=null;
        if(parentId.HasValue) {
            var parent=library.GetItemById(parentId.Value);
            if(parent is not Folder||!parent.IsVisibleStandalone(user)) throw new KeyNotFoundException();
            var ids=candidates.Concat(known).Distinct().ToArray();
            scoped=ids.Length==0?[]:library.GetItemsResult(new InternalItemsQuery(user) { ParentId=parentId.Value,Recursive=true,ItemIds=ids,IncludeItemTypes=[BaseItemKind.Movie,BaseItemKind.Episode],EnableTotalRecordCount=false }).Items.Select(item=>item.Id).ToHashSet();
        }
        foreach(Guid id in candidates) {
            if(scoped!=null&&!scoped.Contains(id)) continue;
            var item=library.GetItemById(id); if(item==null||!SupportedItem(item)||!item.IsVisibleStandalone(user)) continue;
            var state=await Get(user,item).ConfigureAwait(false); if(!state.Active||!state.HasResume||!state.EffectiveRule.PersistentResume||!parentId.HasValue&&!state.ResumeVisible) continue;
            results.Add(new { itemId=id.ToString("N"),state }); if(results.Count>=limit) break;
        }
        // Reconcile already cached cards in the same request, including manual resets and foreign-client starts.
        var states=new List<object>();
        foreach(Guid id in known) {
            if(scoped!=null&&!scoped.Contains(id)) continue;
            var item=library.GetItemById(id); if(item==null||!SupportedItem(item)||!item.IsVisibleStandalone(user)) continue;
            states.Add(new { itemId=id.ToString("N"),state=await Get(user,item).ConfigureAwait(false) });
        }
        return new { items=results,states,parentId=parentId?.ToString("N") };
    }
    public Task<ItemState> UpdateRule(User user,BaseItem item,UpdateRuleRequest request) => store.Locked(user.Id,item.Id,record=> {
        if(request.ExpectedRevision!=record.Revision) throw new ConflictException("State changed. Reload first.");
        request.Rule?.Validate();
        // Old automatic records from 0.1.0 must not resurrect progress which Jellyfin has since cleared.
        // Initialize a first deliberate choice from current native data; retain positions for existing chosen rules.
        if(record.Rule==null&&!Active(Effective(record,item.GetRunTimeTicksForPlayState(),out _))&&request.Rule!=null) {
            var native=data.GetUserData(user,item) ?? throw new InvalidOperationException();
            record.ResumeTicks=Math.Max(0,native.PlaybackPositionTicks); record.ResumeOwned=request.Rule.PersistentResume&&record.ResumeTicks>0; record.Played=native.Played;
            record.NativeItemId=item.Id; record.NativePlayed=native.Played; record.NativePositionTicks=native.PlaybackPositionTicks;
        }
        record.Rule=request.Rule; record.Revision++; record.UpdatedAt=DateTimeOffset.UtcNow; store.Save(record);
        return Task.FromResult(State(record,user,item));
    });
    public Task<MediaRecord> GetGlobal(User user) => store.Locked(user.Id,Guid.Empty,record=>Task.FromResult(record));
    public Task<MediaRecord> UpdateGlobal(User user,long revision,LongVideoRule rule) => store.Locked(user.Id,Guid.Empty,record=> {
        rule.Validate(); if(record.Revision!=revision) throw new ConflictException("State changed. Reload first.");
        record.LongVideo=rule; record.Revision++; record.UpdatedAt=DateTimeOffset.UtcNow; store.Save(record); return Task.FromResult(record);
    });
    private static BaseItem ProgressItem(BaseItem item,string? source) => item is Video video && Guid.TryParse(source,out var id) && id!=item.Id?video.GetAlternateVersion(id)??item:item;
    public Task<PlaybackAck> Report(User user,BaseItem item,SessionInfo session,PlaybackRequest request) => store.Locked<PlaybackAck>(user.Id,item.Id,async record=> {
        if(request.LeaseId==Guid.Empty || request.Sequence<0 || request.Playback.ItemId!=item.Id || request.Playback.PositionTicks<0 || string.IsNullOrEmpty(request.Playback.PlaySessionId)) throw new ArgumentException("Invalid playback report.");
        bool begin=request.Event=="Start",stop=request.Event=="Stopped";
        if(!begin&&!stop&&request.Event!="Progress") throw new ArgumentException("Unknown playback event.");
        if(begin && request.Sequence!=0) throw new ArgumentException("Start sequence must be zero.");
        if(begin&&record.Lease?.Id!=request.LeaseId&&!Active(Effective(record,item.GetRunTimeTicksForPlayState(),out _))) return new(false,false,"disabled",State(record,user,item));
        if(begin && record.Lease?.Id!=request.LeaseId) {
            if(record.Revision!=request.ExpectedRevision) throw new ConflictException("State changed before playback start.");
            record.Revision++; record.ManualFence=false;
            record.Played=(data.GetUserData(user,item) ?? throw new InvalidOperationException()).Played;
            record.Lease=new() { Id=request.LeaseId,DeviceId=session.DeviceId,PlaySessionId=request.Playback.PlaySessionId,Sequence=-1 };
        }
        var lease=record.Lease;
        if(lease is null || lease.Id!=request.LeaseId || lease.DeviceId!=session.DeviceId || lease.PlaySessionId!=request.Playback.PlaySessionId)
            return new(false,false,"superseded",State(record,user,item));
        if(request.Sequence<=lease.Sequence) return new(false,true,lease.DeliveryPending?"deliveryUncertain":null,State(record,user,item));
        if(lease.Stopped) return new(false,false,"stopped",State(record,user,item));
        var progressItem=ProgressItem(item,request.Playback.MediaSourceId);
        record.RuntimeTicks=Math.Max(0,progressItem.GetRunTimeTicksForPlayState());
        long position=Math.Max(0,request.Playback.PositionTicks??record.ResumeTicks);
        if(record.RuntimeTicks>0) position=Math.Min(position,record.RuntimeTicks);
        var rule=Effective(record,record.RuntimeTicks,out _);
        if(!record.ManualFence) {
            record.ResumeTicks=Active(rule)?position:0; record.ResumeOwned=Active(rule)&&rule.PersistentResume;
            if(!begin&&rule.Mode!=CompletionMode.Default) record.Played=record.Played||rule.Completes(position,record.RuntimeTicks);
        }
        lease.Sequence=request.Sequence; lease.DeliveryPending=true; if(stop) lease.Stopped=true;
        record.UpdatedAt=DateTimeOffset.UtcNow;
        // Persist before invoking a non-idempotent core start/stop. An ambiguous replay never increments PlayCount twice.
        store.Save(record);
        request.Playback.SessionId=session.Id;
        using(var scope=new PlaybackScope { User=user,Item=item,ProgressItem=progressItem,Record=record,Rule=rule,Position=position,WritingCorrection=!Active(rule) }) {
            try {
                if(begin) await sessions.OnPlaybackStart(request.Playback).ConfigureAwait(false);
                else if(stop) { await transcodes.KillTranscodingJobs(session.DeviceId,request.Playback.PlaySessionId,_=>true).ConfigureAwait(false); await sessions.OnPlaybackStopped(new PlaybackStopInfo {
                    ItemId=item.Id,SessionId=session.Id,MediaSourceId=request.Playback.MediaSourceId,PlaySessionId=request.Playback.PlaySessionId,
                    PositionTicks=request.Playback.PositionTicks,LiveStreamId=request.Playback.LiveStreamId,Failed=request.Failed,
                    NowPlayingQueue=request.Playback.NowPlayingQueue,PlaylistItemId=request.Playback.PlaylistItemId
                }).ConfigureAwait(false); }
                else await sessions.OnPlaybackProgress(request.Playback).ConfigureAwait(false);
            } finally { session.StopAutomaticProgress(); }
        }
        if(rule.Mode==CompletionMode.Default&&!record.ManualFence) record.Played=(data.GetUserData(user,progressItem) ?? throw new InvalidOperationException()).Played;
        var snapshot=data.GetUserData(user,progressItem) ?? throw new InvalidOperationException(); record.NativeItemId=progressItem.Id; record.NativePlayed=snapshot.Played; record.NativePositionTicks=snapshot.PlaybackPositionTicks;
        lease.DeliveryPending=false; store.Save(record); return new(true,false,null,State(record,user,item));
    });
    public Task<ItemState> Action(User user,BaseItem item,ActionRequest request) { long ordinal=order.Next(); return store.Locked(user.Id,item.Id,record=> {
        if(record.Revision!=request.ExpectedRevision) throw new ConflictException("State changed. Reload first.");
        var native=data.GetUserData(user,item) ?? throw new InvalidOperationException();
        if(request.Action!="ResetResume"&&request.Action!="MarkSeen"&&request.Action!="MarkUnseen") throw new ArgumentException("Unknown action.");
        if(!order.Newer(record,ordinal)) throw new ConflictException("A newer manual action already applied."); order.Apply(record,ordinal);
        record.Revision++; record.ManualFence=true; record.ResumeOwned=true; record.ResumeTicks=0;
        record.Played=request.Action=="MarkSeen" || request.Action=="ResetResume"&&native.Played;
        record.UpdatedAt=DateTimeOffset.UtcNow; store.Save(record);
        using(var scope=new PlaybackScope { User=user,Item=item,ProgressItem=item,Record=record,Rule=Effective(record,item.GetRunTimeTicksForPlayState(),out _),WritingCorrection=true }) {
            if(request.Action=="MarkSeen") item.MarkPlayed(user,null,true);
            else if(request.Action=="MarkUnseen") item.MarkUnplayed(user);
            else { native.PlaybackPositionTicks=0; data.SaveUserData(user,item,native,UserDataSaveReason.UpdateUserData,CancellationToken.None); }
        }
        return Task.FromResult(State(record,user,item));
    }); }
    internal void Correct(PlaybackScope scope,UserDataSaveEventArgs args) {
        if(scope.WritingCorrection || !scope.Matches(args.UserId,args.Item)) return;
        if(args.SaveReason is not (UserDataSaveReason.PlaybackStart or UserDataSaveReason.PlaybackProgress or UserDataSaveReason.PlaybackFinished)) return;
        var record=scope.Record; bool played=record.ManualFence||scope.Rule.Mode!=CompletionMode.Default?record.Played:args.UserData.Played;
        long position=record.ManualFence?record.ResumeTicks:scope.Rule.Mode!=CompletionMode.Default?(played?0:args.UserData.Played?scope.Position:args.UserData.PlaybackPositionTicks):args.UserData.PlaybackPositionTicks;
        record.Played=played;
        if(args.UserData.Played==played&&args.UserData.PlaybackPositionTicks==position) return;
        // Mutate the exact object core checks after the synchronous event, then persist via the supported API.
        args.UserData.Played=played; args.UserData.PlaybackPositionTicks=position;
        scope.WritingCorrection=true;
        try { data.SaveUserData(scope.User,args.Item,args.UserData,UserDataSaveReason.PlaybackProgress,CancellationToken.None); }
        finally { scope.WritingCorrection=false; }
    }
}
