using Jellyfin.Database.Implementations.Entities;
using Lowfin.Companion;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Lowfin.Companion.Tests;

public sealed class PlaybackTests : IDisposable {
    private readonly string directory=Path.Combine(Path.GetTempPath(),"lowfin-companion-"+Guid.NewGuid());
    private readonly CompanionStore store;
    private readonly Mock<IUserDataManager> data=new();
    private readonly Mock<ISessionManager> sessions=new();
    private readonly Mock<IUserManager> users=new();
    private readonly Mock<ITranscodeManager> transcodes=new();
    private readonly User user=new("rico","auth","reset");
    private readonly Movie movie=new() { Id=Guid.NewGuid(),RunTimeTicks=TimeSpan.FromHours(12).Ticks };
    private readonly CompanionService service;
    private readonly CompanionEvents events;
    private readonly SessionInfo session;
    private UserItemData native=new() { Key="movie" };
    private Action<UserDataSaveReason>? beforeSaved;
    public PlaybackTests() {
        store=new(directory);
        // Service and listener share the same manual-action clock, as in DI.
        var clock=new ActionOrder(); service=new(store,data.Object,sessions.Object,transcodes.Object,clock);
        events=new(service,data.Object,users.Object,sessions.Object,clock,NullLogger<CompanionEvents>.Instance);
        users.Setup(x=>x.GetUserById(user.Id)).Returns(user);
        data.Setup(x=>x.GetUserData(user,It.IsAny<BaseItem>())).Returns(()=>new UserItemData { Key="movie",Played=native.Played,PlaybackPositionTicks=native.PlaybackPositionTicks,PlayCount=native.PlayCount });
        data.Setup(x=>x.SaveUserData(user,It.IsAny<BaseItem>(),It.IsAny<UserItemData>(),It.IsAny<UserDataSaveReason>(),It.IsAny<CancellationToken>())).Callback<User,BaseItem,UserItemData,UserDataSaveReason,CancellationToken>((u,item,value,reason,_)=> {
            native=value; beforeSaved?.Invoke(reason); data.Raise(x=>x.UserDataSaved+=null,new UserDataSaveEventArgs { UserId=user.Id,Item=item,UserData=value,SaveReason=reason });
        });
        sessions.Setup(x=>x.OnPlaybackStart(It.IsAny<PlaybackStartInfo>())).Returns<PlaybackStartInfo>(info=> { native.PlayCount++; SaveNative(UserDataSaveReason.PlaybackStart); return Task.CompletedTask; });
        sessions.Setup(x=>x.OnPlaybackProgress(It.IsAny<PlaybackProgressInfo>())).Returns<PlaybackProgressInfo>(info=> { SimulateGlobal(info.PositionTicks??0); return Task.CompletedTask; });
        sessions.Setup(x=>x.OnPlaybackStopped(It.IsAny<PlaybackStopInfo>())).Returns<PlaybackStopInfo>(info=> { SimulateGlobal(info.PositionTicks??0); return Task.CompletedTask; });
        transcodes.Setup(x=>x.KillTranscodingJobs(It.IsAny<string>(),It.IsAny<string>(),It.IsAny<Func<string,bool>>())).Returns(Task.CompletedTask);
        session=new(sessions.Object,NullLogger.Instance) { Id="core-session",DeviceId="tv-1",UserId=user.Id,Client="Lowfin" };
        events.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
    }
    private void SaveNative(UserDataSaveReason reason) => data.Object.SaveUserData(user,movie,native,reason,CancellationToken.None);
    private void SimulateGlobal(long position) {
        native=data.Object.GetUserData(user,movie)!; // Core reads a mapped DTO, not the object held by another save callback.
        native.PlaybackPositionTicks=position;
        if(position>movie.RunTimeTicks!.Value*0.9) { native.Played=true; native.PlaybackPositionTicks=0; }
        SaveNative(UserDataSaveReason.PlaybackProgress);
    }
    private PlaybackRequest Request(Guid lease,long sequence,string type,long position,long revision=1) => new() {
        LeaseId=lease,Sequence=sequence,ExpectedRevision=revision,Event=type,
        Playback=new() { ItemId=movie.Id,MediaSourceId=movie.Id.ToString("N"),PlaySessionId=lease.ToString(),PositionTicks=position }
    };
    private async Task<Guid> Begin(CompletionMode mode=CompletionMode.RemainingTime) {
        await service.UpdateRule(user,movie,new(0,new() { Mode=mode })); var id=Guid.NewGuid();
        await service.Report(user,movie,session,Request(id,0,"Start",0)); return id;
    }
    [Fact] public async Task CorePercentageCompletionIsCorrectedOnlyInOwnedScope() {
        var lease=await Begin(); long position=TimeSpan.FromHours(11).Ticks;
        await service.Report(user,movie,session,Request(lease,1,"Progress",position));
        Assert.False(native.Played); Assert.Equal(position,native.PlaybackPositionTicks);
        var state=await service.Get(user,movie); Assert.Equal(position,state.ResumeTicks); Assert.True(state.HasResume);
        // An unrelated client's native save is never intercepted merely because the same user/item has a rule.
        native.Played=true; native.PlaybackPositionTicks=0; SaveNative(UserDataSaveReason.PlaybackProgress); Assert.True(native.Played);
    }
    [Fact] public async Task CompletedMediaRetainsIndependentResume() {
        var lease=await Begin(); long position=movie.RunTimeTicks!.Value-TimeSpan.FromMinutes(1).Ticks;
        await service.Report(user,movie,session,Request(lease,1,"Progress",position));
        Assert.True(native.Played); Assert.Equal(0,native.PlaybackPositionTicks);
        Assert.Equal(position,(await service.Get(user,movie)).ResumeTicks);
    }
    [Fact] public async Task NewSequenceCanSeekBackwardButOldReportsCannotOverwrite() {
        var lease=await Begin(); await service.Report(user,movie,session,Request(lease,3,"Progress",TimeSpan.FromHours(8).Ticks));
        var duplicate=await service.Report(user,movie,session,Request(lease,2,"Progress",TimeSpan.FromHours(3).Ticks)); Assert.True(duplicate.Duplicate);
        await service.Report(user,movie,session,Request(lease,4,"Progress",TimeSpan.FromHours(4).Ticks));
        Assert.Equal(TimeSpan.FromHours(4).Ticks,(await service.Get(user,movie)).ResumeTicks);
    }
    [Fact] public async Task ManualWatchedAndResetFenceInFlightPlayerReports() {
        var lease=await Begin(CompletionMode.NeverAutoComplete); await service.Report(user,movie,session,Request(lease,1,"Progress",TimeSpan.FromHours(10).Ticks));
        native.Played=true; native.PlaybackPositionTicks=0; SaveNative(UserDataSaveReason.TogglePlayed);
        await service.Report(user,movie,session,Request(lease,2,"Progress",TimeSpan.FromHours(11).Ticks));
        Assert.True(native.Played); Assert.Equal(0,(await service.Get(user,movie)).ResumeTicks);
        var state=await service.Get(user,movie); await service.Action(user,movie,new(state.Revision,"ResetResume"));
        await service.Report(user,movie,session,Request(lease,3,"Stopped",TimeSpan.FromHours(11.5).Ticks));
        Assert.Equal(0,(await service.Get(user,movie)).ResumeTicks); Assert.True(native.Played);
    }
    [Fact] public async Task NewPlaybackLeaseRejectsOldSessionStop() {
        var first=await Begin(); var state=await service.Get(user,movie); var second=Guid.NewGuid();
        await service.Report(user,movie,session,Request(second,0,"Start",TimeSpan.FromHours(5).Ticks,state.Revision));
        var old=await service.Report(user,movie,session,Request(first,99,"Stopped",TimeSpan.FromHours(11).Ticks));
        Assert.False(old.Accepted); Assert.Equal("superseded",old.Reason); Assert.Equal(TimeSpan.FromHours(5).Ticks,(await service.Get(user,movie)).ResumeTicks);
    }
    [Fact] public async Task RepeatedStartIsIdempotentAndProgressSurvivesServiceRestart() {
        var lease=await Begin(); await service.Report(user,movie,session,Request(lease,0,"Start",0)); Assert.Equal(1,native.PlayCount);
        await service.Report(user,movie,session,Request(lease,1,"Progress",TimeSpan.FromHours(10).Ticks));
        var restarted=new CompanionService(new CompanionStore(directory),data.Object,sessions.Object,transcodes.Object,new ActionOrder());
        Assert.Equal(TimeSpan.FromHours(10).Ticks,(await restarted.Get(user,movie)).ResumeTicks);
    }
    [Fact] public async Task UnknownDeliveryDoesNotRepeatANonIdempotentStart() {
        await service.UpdateRule(user,movie,new(0,new() { Mode=CompletionMode.NeverAutoComplete }));
        sessions.Setup(x=>x.OnPlaybackStart(It.IsAny<PlaybackStartInfo>())).Returns<PlaybackStartInfo>(_=> { native.PlayCount++; throw new IOException("Simulated disconnect after delivery."); });
        var lease=Guid.NewGuid(); await Assert.ThrowsAsync<IOException>(()=>service.Report(user,movie,session,Request(lease,0,"Start",0)));
        var retry=await service.Report(user,movie,session,Request(lease,0,"Start",0)); Assert.Equal("deliveryUncertain",retry.Reason); Assert.Equal(1,native.PlayCount);
    }
    [Fact] public async Task CrashCleanupCannotAdvanceRealProgressOrCompleteLongVideo() {
        var lease=await Begin(); long position=TimeSpan.FromHours(11).Ticks; await service.Report(user,movie,session,Request(lease,1,"Progress",position));
        native.Played=true; native.PlaybackPositionTicks=0;
        sessions.Raise(x=>x.PlaybackStopped+=null,new PlaybackStopEventArgs { Item=movie,Users=[user],DeviceId=session.DeviceId,PlaySessionId=lease.ToString(),PlaybackPositionTicks=movie.RunTimeTicks });
        Assert.False(native.Played); Assert.Equal(position,(await service.Get(user,movie)).ResumeTicks);
    }
    [Fact] public async Task ForeignClientStartRetiresLowfinPositionOwnership() {
        var lease=await Begin(); await service.Report(user,movie,session,Request(lease,1,"Progress",TimeSpan.FromHours(11).Ticks));
        native.PlaybackPositionTicks=TimeSpan.FromMinutes(1).Ticks;
        sessions.Raise(x=>x.PlaybackStart+=null,new PlaybackProgressEventArgs { Item=movie,Users=[user],DeviceId="other-device",PlaySessionId="other-session",ClientName="Other client" });
        var old=await service.Report(user,movie,session,Request(lease,2,"Progress",TimeSpan.FromHours(11.5).Ticks)); Assert.False(old.Accepted);
        Assert.Equal(TimeSpan.FromMinutes(1).Ticks,(await service.Get(user,movie)).ResumeTicks);
    }
    [Fact] public async Task DelayedOwnedStartNotificationKeepsPlaybackLease() {
        var lease=await Begin();
        sessions.Raise(x=>x.PlaybackStart+=null,new PlaybackProgressEventArgs { Item=movie,Users=[user],DeviceId=session.DeviceId,PlaySessionId=lease.ToString(),ClientName="Lowfin" });
        var progress=await service.Report(user,movie,session,Request(lease,1,"Progress",TimeSpan.FromHours(11).Ticks));
        Assert.True(progress.Accepted); Assert.False(progress.State.Played);
        Assert.Equal(TimeSpan.FromHours(11).Ticks,progress.State.ResumeTicks);
    }
    [Fact] public async Task ManualWatchedCommittedDuringProgressWinsAfterTheActorLock() {
        var lease=await Begin();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var manualCommitted=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        beforeSaved=reason=> { if(reason==UserDataSaveReason.TogglePlayed) manualCommitted.TrySetResult(); };
        sessions.Setup(x=>x.OnPlaybackProgress(It.IsAny<PlaybackProgressInfo>())).Returns<PlaybackProgressInfo>(async info=> { entered.SetResult(); await release.Task; SimulateGlobal(info.PositionTicks??0); });
        var progress=service.Report(user,movie,session,Request(lease,1,"Progress",TimeSpan.FromHours(11).Ticks));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var manual=Task.Run(()=>data.Object.SaveUserData(user,movie,new UserItemData { Key="movie",Played=true,PlaybackPositionTicks=0 },UserDataSaveReason.TogglePlayed,CancellationToken.None));
        await manualCommitted.Task.WaitAsync(TimeSpan.FromSeconds(5)); release.SetResult();
        await Task.WhenAll(progress,manual).WaitAsync(TimeSpan.FromSeconds(5));
        var state=await service.Get(user,movie); Assert.True(native.Played); Assert.True(state.Played); Assert.True(state.ManualFence); Assert.Equal(0,state.ResumeTicks);
    }
    [Fact] public async Task ExplicitDefaultOverridesTheUsersLongVideoFallback() {
        await service.UpdateGlobal(user,0,new() { Enabled=true,MinimumSeconds=14400,Rule=new() { Mode=CompletionMode.RemainingTime } });
        var inherited=await service.Get(user,movie); Assert.True(inherited.UsesLongVideoRule); Assert.Equal(CompletionMode.RemainingTime,inherited.EffectiveRule.Mode);
        var own=await service.UpdateRule(user,movie,new(inherited.Revision,new() { Mode=CompletionMode.Default }));
        Assert.False(own.UsesLongVideoRule); Assert.Equal(CompletionMode.Default,own.EffectiveRule.Mode);
    }
    [Fact] public async Task ResumeIndexPersistsAndClearsManualResetWithoutCrossUserEntries() {
        var lease=await Begin(); await service.Report(user,movie,session,Request(lease,1,"Progress",TimeSpan.FromHours(11).Ticks));
        Assert.Contains(movie.Id,store.ResumeCandidates(user.Id)); Assert.Empty(store.ResumeCandidates(Guid.NewGuid()));
        Assert.Contains(movie.Id,new CompanionStore(directory).ResumeCandidates(user.Id));
        var state=await service.Get(user,movie); await service.Action(user,movie,new(state.Revision,"ResetResume"));
        Assert.Empty(store.ResumeCandidates(user.Id)); Assert.Empty(new CompanionStore(directory).ResumeCandidates(user.Id));
    }
    [Fact] public async Task UnconfiguredMediaUsesNativeResumeAndDoesNotStartCompanionPlayback() {
        native.PlaybackPositionTicks=TimeSpan.FromHours(4).Ticks;
        var state=await service.Get(user,movie); Assert.False(state.Active); Assert.False(state.EffectiveRule.PersistentResume); Assert.Equal(native.PlaybackPositionTicks,state.ResumeTicks);
        var result=await service.Report(user,movie,session,Request(Guid.NewGuid(),0,"Start",0,0));
        Assert.False(result.Accepted); Assert.Equal("disabled",result.Reason); Assert.Equal(0,native.PlayCount);
        sessions.Verify(x=>x.OnPlaybackStart(It.IsAny<PlaybackStartInfo>()),Times.Never()); Assert.Empty(store.ResumeCandidates(user.Id));
    }
    [Fact] public async Task ExplicitNativeDefaultCanDisableCompanionWithoutRemovingTheGeneralOverride() {
        await service.UpdateGlobal(user,0,new() { Enabled=true,MinimumSeconds=14400,Rule=new() { Mode=CompletionMode.RemainingTime } });
        Assert.True((await service.Get(user,movie)).Active);
        var state=await service.UpdateRule(user,movie,new(0,new() { Mode=CompletionMode.Default,PersistentResume=false }));
        Assert.False(state.Active); Assert.False(state.UsesLongVideoRule);
        await service.UpdateRule(user,movie,new(state.Revision,null)); Assert.True((await service.Get(user,movie)).Active);
    }
    [Fact] public async Task RuleIsSharedAcrossDeviceServicesButNotAcrossUsers() {
        await service.UpdateRule(user,movie,new(0,new() { Mode=CompletionMode.CustomPercentage,Percentage=99.5m }));
        var bedroom=new CompanionService(new CompanionStore(directory),data.Object,sessions.Object,transcodes.Object,new ActionOrder());
        var shared=await bedroom.Get(user,movie); Assert.True(shared.Active); Assert.Equal(99.5m,shared.EffectiveRule.Percentage);
        var other=new User("other","auth","reset"); data.Setup(x=>x.GetUserData(other,It.IsAny<BaseItem>())).Returns(new UserItemData { Key="movie" });
        Assert.False((await bedroom.Get(other,movie)).Active);
    }
    [Fact] public async Task FirstRuleDoesNotResurrectALegacyAutomaticPositionAfterNativeClear() {
        native.Played=true; native.PlaybackPositionTicks=0;
        await store.Locked(user.Id,movie.Id,record=> { record.ResumeOwned=true; record.ResumeTicks=TimeSpan.FromHours(11).Ticks; store.Save(record); return Task.FromResult(true); });
        Assert.False((await service.Get(user,movie)).Active);
        var state=await service.UpdateRule(user,movie,new(0,new() { Mode=CompletionMode.CustomPercentage,Percentage=99.5m }));
        Assert.True(state.Active); Assert.False(state.HasResume); Assert.Equal(0,state.ResumeTicks); Assert.True(native.Played);
    }
    public void Dispose() { events.StopAsync(CancellationToken.None).GetAwaiter().GetResult(); session.DisposeAsync().AsTask().GetAwaiter().GetResult(); if(Directory.Exists(directory)) Directory.Delete(directory,true); }
}
