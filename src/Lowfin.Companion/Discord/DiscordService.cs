using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.LiveTv;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
namespace Lowfin.Companion.Discord;
public sealed record RpcUpdate(Guid User,BaseItem Item,long Ticks,bool Paused,bool Stopped);
/// <summary>Coalesce events per user; no extra playback reports or completion mutations.</summary>
public sealed class DiscordService(ISessionManager playback,ILibraryManager library,IUserManager users,IDiscordGatewayFactory gateways,DiscordArtwork artwork,ILogger<DiscordService> log,Func<DiscordOptions>? readOptions=null,TimeProvider? clock=null,DiscordTokenProtection? tokens=null) : BackgroundService {
    private sealed class Session(IDiscordGateway connection,string credentials,string token) {
        public IDiscordGateway Connection=connection;public string Credentials=credentials;public string Token=token;
        public Guid Item;public bool Paused;public long Start;public DateTimeOffset LastSeen,LastSent,NextProgramme;public Programme? Programme,SentProgramme;public string? Image;public DateTimeOffset NextArtwork;public string ArtworkOptions="";
    }
    private readonly ConcurrentDictionary<Guid,RpcUpdate> pending=new();
    private readonly Channel<bool> signal=Channel.CreateBounded<bool>(new BoundedChannelOptions(1) {FullMode=BoundedChannelFullMode.DropWrite});
    private readonly Dictionary<Guid,Session> sessions=new();
    private DiscordOptions Options=>readOptions?.Invoke()??Plugin.Instance?.Configuration.DiscordRpc??new();
    private void Wake()=>signal.Writer.TryWrite(true);
    private void Event(object? sender,PlaybackProgressEventArgs args)=>Queue(args,false);
    private void Stopped(object? sender,PlaybackStopEventArgs args)=>Queue(args,true);
    private void Queue(PlaybackProgressEventArgs args,bool stopped) {
        if(args.Item is null||args.Users is null)return;var options=Options;
        foreach(var user in args.Users) if(options.Enabled&&options.User(user.Id) is not null) pending[user.Id]=new(user.Id,args.Item,args.PlaybackPositionTicks??0,args.IsPaused,stopped);
        Wake();
    }
    public override Task StartAsync(CancellationToken cancellation) {log.LogInformation("Discord RPC playback listener started; enabled: {Enabled}, enabled user mappings: {Mappings}, application ID set: {ApplicationIdSet}.",Options.Enabled,Options.Users.Count(x=>x.Enabled&&!string.IsNullOrWhiteSpace(x.DiscordToken)),!string.IsNullOrWhiteSpace(Options.ApplicationId));playback.PlaybackStart+=Event;playback.PlaybackProgress+=Event;playback.PlaybackStopped+=Stopped;if(Plugin.Instance is {} plugin)plugin.SettingsChanged+=Wake;return base.StartAsync(cancellation);}
    protected override async Task ExecuteAsync(CancellationToken cancellation) {
        using var timerLifetime=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var timer=Tick(timerLifetime.Token);
        try {while(await signal.Reader.WaitToReadAsync(cancellation).ConfigureAwait(false)) {
            while(signal.Reader.TryRead(out _)){}
            var options=Options;var now=clock?.GetUtcNow()??DateTimeOffset.UtcNow;
            foreach(var id in sessions.Keys.ToArray()) if(!options.Enabled||string.IsNullOrWhiteSpace(options.ApplicationId)||options.User(id) is null||options.IdleTimeoutSeconds>0&&sessions[id].LastSeen<now.AddSeconds(-Math.Clamp(options.IdleTimeoutSeconds,30,3600)))await Remove(id).ConfigureAwait(false);
            foreach(var id in pending.Keys) if(pending.TryRemove(id,out var update))try {await Report(update,options,now,cancellation).ConfigureAwait(false);}catch(OperationCanceledException) when(cancellation.IsCancellationRequested){throw;}catch(Exception error){log.LogWarning("Discord RPC update skipped ({Kind}).",error.GetType().Name);}
        }} catch(OperationCanceledException) when(cancellation.IsCancellationRequested){}
        finally {timerLifetime.Cancel();foreach(var id in sessions.Keys.ToArray())await Remove(id).ConfigureAwait(false);try {await timer.ConfigureAwait(false);}catch(OperationCanceledException){}}
    }
    private async Task Tick(CancellationToken cancellation) {using var timer=new PeriodicTimer(TimeSpan.FromSeconds(15));while(await timer.WaitForNextTickAsync(cancellation).ConfigureAwait(false))Wake();}
    private async Task Remove(Guid user) {if(sessions.Remove(user,out var state))await state.Connection.DisposeAsync().ConfigureAwait(false);}
    private async Task Report(RpcUpdate update,DiscordOptions options,DateTimeOffset now,CancellationToken cancellation) {
        var mapping=options.User(update.User);if(!options.Enabled||mapping is null||string.IsNullOrWhiteSpace(options.ApplicationId))return;
        if(update.Stopped) {if(sessions.TryGetValue(update.User,out var current)&&current.Item==update.Item.Id)await Remove(update.User).ConfigureAwait(false);return;}
        if(!DiscordActivity.Supported(update.Item,options)||update.Paused&&!options.ShowPaused){await Remove(update.User).ConfigureAwait(false);return;}
        string credentials=options.ApplicationId+"\n"+mapping.DiscordToken;
        if(sessions.TryGetValue(update.User,out var previous)&&previous.Credentials!=credentials)await Remove(update.User).ConfigureAwait(false);
        if(!sessions.TryGetValue(update.User,out var session)) {string token;
            try {if(tokens is null&&DiscordTokenProtection.IsProtected(mapping.DiscordToken))throw new InvalidOperationException("Token protector unavailable");token=tokens?.Read(mapping.JellyfinUserId,mapping.DiscordToken)??mapping.DiscordToken;}
            catch(Exception error){log.LogError("Discord RPC token cannot be decrypted for user {UserId} ({Kind}). Restore the Companion secret-keys directory or enter the token again; encrypted configuration is retained.",update.User,error.GetType().Name);return;}
            session=new(gateways.Create(token),credentials,token);sessions.Add(update.User,session);}
        session.LastSeen=now;long start=now.ToUnixTimeMilliseconds()-Math.Max(0,update.Ticks)/10000;
        string artworkOptions=string.Join('|',options.UploadArtwork,options.ArtworkUploadUrl,options.FallbackImageUrl);
        bool artworkOptionsChanged=session.ArtworkOptions!=artworkOptions;
        bool artworkDue=session.Item!=update.Item.Id||artworkOptionsChanged||((options.UploadArtwork||!string.IsNullOrWhiteSpace(options.FallbackImageUrl))&&now>=session.NextArtwork);
        bool changed=session.Item!=update.Item.Id||session.Paused!=update.Paused||Math.Abs(start-session.Start)>3000;
        if(session.Item!=update.Item.Id){session.NextProgramme=DateTimeOffset.MinValue;session.Programme=null;session.Image=null;}
        if(update.Item is LiveTvChannel channel&&now>=session.NextProgramme) {
            session.NextProgramme=now.AddSeconds(30);session.Programme=FindProgramme(update.User,channel,now.UtcDateTime);
            if(session.Programme is {} programme&&programme.End<session.NextProgramme.UtcDateTime)session.NextProgramme=new DateTimeOffset(DateTime.SpecifyKind(programme.End,DateTimeKind.Utc));
        }
        if(!changed&&Equals(session.Programme,session.SentProgramme)&&!artworkDue)return;
        if(session.Item==update.Item.Id&&session.Paused==update.Paused&&now-session.LastSent<TimeSpan.FromSeconds(Math.Clamp(options.MinUpdateIntervalSeconds,5,300)))return;
        var previousImage=session.Image;
        if(artworkDue) {
            session.Image=await artwork.Resolve(update.Item,options,session.Token,cancellation).ConfigureAwait(false);
            session.NextArtwork=now.AddMinutes(session.Image is null?2:15);session.ArtworkOptions=artworkOptions;
        }
        if(!changed&&Equals(session.Programme,session.SentProgramme)&&session.Image==previousImage)return;
        var pausedImage=update.Paused?await artwork.ResolvePublic(options.PauseImageUrl,options.ApplicationId,session.Token,cancellation).ConfigureAwait(false):null;
        var activity=DiscordActivity.Build(update.Item,update.Ticks,update.Paused,options,now,session.Programme,session.Image,pausedImage);
        string status=options.OnlineStatus is "online" or "idle" or "invisible"?options.OnlineStatus:"dnd";
        session.Connection.Update(new JsonObject {["activities"]=new JsonArray(activity),["status"]=status,["since"]=0,["afk"]=false});
        session.Item=update.Item.Id;session.Start=start;session.Paused=update.Paused;session.LastSent=now;session.SentProgramme=session.Programme;
    }
    private Programme? FindProgramme(Guid userId,LiveTvChannel channel,DateTime now) {
        try {var user=users.GetUserById(userId);if(user is null)return null;
            var programme=library.GetItemList(new InternalItemsQuery(user) {ChannelIds=[channel.Id],IncludeItemTypes=[BaseItemKind.LiveTvProgram],MaxStartDate=now,MinEndDate=now,Limit=1,EnableTotalRecordCount=false,OrderBy=[(ItemSortBy.StartDate,SortOrder.Descending)]}).OfType<LiveTvProgram>().FirstOrDefault();
            return programme is not null&&programme.ChannelId==channel.Id&&programme.StartDate<=now&&programme.EndDate is DateTime end&&end>now&&!string.IsNullOrWhiteSpace(programme.Name)?new(programme.Name.Trim(),programme.StartDate,end):null;
        }catch(Exception error){log.LogDebug("Discord EPG lookup skipped ({Kind}).",error.GetType().Name);return null;}
    }
    public override async Task StopAsync(CancellationToken cancellation) {playback.PlaybackStart-=Event;playback.PlaybackProgress-=Event;playback.PlaybackStopped-=Stopped;if(Plugin.Instance is {} plugin)plugin.SettingsChanged-=Wake;await base.StopAsync(cancellation).ConfigureAwait(false);}
}
