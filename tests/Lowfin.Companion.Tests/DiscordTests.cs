using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Lowfin.Companion.Discord;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Jellyfin.Database.Implementations.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
namespace Lowfin.Companion.Tests;
public sealed class DiscordTests {
    private static readonly DateTimeOffset Now=new(2026,10,26,12,0,0,TimeSpan.Zero);
    [Fact] public void DisabledByDefaultAndUserMappingsAreExplicit() {
        var options=new DiscordOptions();Assert.False(options.Enabled);Assert.False(options.UploadArtwork);Assert.Null(options.User(Guid.NewGuid()));
        var id=Guid.NewGuid();options.Users=[new(){JellyfinUserId=id.ToString("N"),DiscordToken="fake",Enabled=false}];Assert.Null(options.User(id));options.Users[0].Enabled=true;Assert.NotNull(options.User(id));Assert.Null(options.User(Guid.NewGuid()));
    }
    [Fact] public void MovieEpisodeTemplatesLinksAndPauseKeepExpectedPayload() {
        var movie=new Movie {Name="Film",RunTimeTicks=TimeSpan.FromHours(2).Ticks,ProductionYear=2026,Genres=["Drama"]};movie.ProviderIds["Imdb"]="tt123";movie.ProviderIds["Tmdb"]="456";
        var options=new DiscordOptions {ApplicationId="123",ShowLinkButton=true};var result=DiscordActivity.Build(movie,TimeSpan.FromMinutes(5).Ticks,false,options,Now);
        Assert.Equal("Film",result["details"]!.GetValue<string>());Assert.Equal("Drama",result["state"]!.GetValue<string>());Assert.Equal(Now.AddMinutes(-5).ToUnixTimeMilliseconds(),result["timestamps"]!["start"]!.GetValue<long>());Assert.Equal(2,result["buttons"]!.AsArray().Count);
        var episode=new Episode {Name="Chapter",SeriesName="Show",ParentIndexNumber=40,IndexNumber=8};options.NameMode=RpcNameMode.Custom;options.CustomActivityName="{series} · {season} / {episode}";
        result=DiscordActivity.Build(episode,0,true,options,Now);Assert.Equal("Show · 40 / 8",result["name"]!.GetValue<string>());Assert.Equal("S40E08 Chapter",result["state"]!.GetValue<string>());Assert.Null(result["timestamps"]);
    }
    [Fact] public void LiveTvUsesGuideTimesAndFallsBackToChannelTitle() {
        var channel=new LiveTvChannel {Name="Channel"};var options=new DiscordOptions();var programme=new Programme("Current show",Now.AddMinutes(-10).UtcDateTime,Now.AddMinutes(20).UtcDateTime);
        var result=DiscordActivity.Build(channel,0,false,options,Now,programme);Assert.Equal("Channel",result["details"]!.GetValue<string>());Assert.Equal("Current show",result["state"]!.GetValue<string>());Assert.Equal(Now.AddMinutes(-10).ToUnixTimeMilliseconds(),result["timestamps"]!["start"]!.GetValue<long>());Assert.Equal(Now.AddMinutes(20).ToUnixTimeMilliseconds(),result["timestamps"]!["end"]!.GetValue<long>());
        result=DiscordActivity.Build(channel,0,true,options,Now,null);Assert.Equal("Channel",result["state"]!.GetValue<string>());Assert.Null(result["timestamps"]);
    }
    [Fact] public void FutureJellyfinVersionsAreNotRejectedOnlyForBeingNewer() {foreach(var version in new[]{new Version(12,2),new Version(12,3),new Version(13,0)})Assert.True(new HostCompatibility(version).Supported);Assert.False(new HostCompatibility(new Version(12,1)).Supported);}
    private sealed class Clock : TimeProvider {public DateTimeOffset Now=DiscordTests.Now;public override DateTimeOffset GetUtcNow()=>Now;}
    private sealed class FakeGateway : IDiscordGateway {public ConcurrentQueue<JsonObject> Updates=new();public bool Closed;public void Update(JsonObject value)=>Updates.Enqueue(value);public ValueTask DisposeAsync(){Closed=true;return ValueTask.CompletedTask;}}
    private sealed class Factory : IDiscordGatewayFactory {public ConcurrentBag<FakeGateway> Connections=new();public string? LastToken;public IDiscordGateway Create(string token){LastToken=token;var value=new FakeGateway();Connections.Add(value);return value;}}
    private static async Task Wait(Func<bool> condition){for(int n=0;n<200&&!condition();n++)await Task.Delay(10);Assert.True(condition());}
    [Fact] public async Task LifecycleUsesOnlyMappedUsersAndClearsStopAndDisabledConfiguration() {
        var user=new User("rpc-test","auth","reset"){Id=Guid.NewGuid()};var other=new User("other","auth","reset"){Id=Guid.NewGuid()};var options=new DiscordOptions {Enabled=true,ApplicationId="123",Users=[new(){JellyfinUserId=user.Id.ToString(),DiscordToken="fake-test-token"}]};
        var playback=new Mock<ISessionManager>();var library=new Mock<ILibraryManager>();var users=new Mock<IUserManager>();var factory=new Factory();var clients=new Mock<IHttpClientFactory>();clients.Setup(x=>x.CreateClient(It.IsAny<string>())).Returns(()=>new HttpClient());var clock=new Clock();
        using var service=new DiscordService(playback.Object,library.Object,users.Object,factory,new DiscordArtwork(library.Object,clients.Object),NullLogger<DiscordService>.Instance,()=>options,clock);await service.StartAsync(default);
        var movie=new Movie {Id=Guid.NewGuid(),Name="Film",RunTimeTicks=TimeSpan.FromHours(1).Ticks};
        try {
            playback.Raise(x=>x.PlaybackStart+=null,new PlaybackProgressEventArgs {Users=[other],Item=movie});await Task.Delay(50);Assert.Empty(factory.Connections);
            playback.Raise(x=>x.PlaybackStart+=null,new PlaybackProgressEventArgs {Users=[user],Item=movie});await Wait(()=>factory.Connections.Count==1&&factory.Connections.First().Updates.Count==1);var first=factory.Connections.First();
            clock.Now=clock.Now.AddSeconds(10);playback.Raise(x=>x.PlaybackProgress+=null,new PlaybackProgressEventArgs {Users=[user],Item=movie,PlaybackPositionTicks=TimeSpan.FromSeconds(10).Ticks});await Task.Delay(50);Assert.Single(first.Updates);
            playback.Raise(x=>x.PlaybackProgress+=null,new PlaybackProgressEventArgs {Users=[user],Item=movie,PlaybackPositionTicks=TimeSpan.FromSeconds(10).Ticks,IsPaused=true});await Wait(()=>first.Updates.Count==2);Assert.Null(first.Updates.Last()["activities"]![0]!["timestamps"]);
            clock.Now=clock.Now.AddSeconds(1);playback.Raise(x=>x.PlaybackProgress+=null,new PlaybackProgressEventArgs {Users=[user],Item=movie,PlaybackPositionTicks=TimeSpan.FromSeconds(10).Ticks,IsPaused=false});await Wait(()=>first.Updates.Count==3);Assert.NotNull(first.Updates.Last()["activities"]![0]!["timestamps"]);
            var next=new Movie {Id=Guid.NewGuid(),Name="Next film"};
            playback.Raise(x=>x.PlaybackStart+=null,new PlaybackProgressEventArgs {Users=[user],Item=next});await Wait(()=>first.Updates.Count==4);
            playback.Raise(x=>x.PlaybackStopped+=null,new PlaybackStopEventArgs {Users=[user],Item=movie});await Task.Delay(50);Assert.False(first.Closed);
            playback.Raise(x=>x.PlaybackStopped+=null,new PlaybackStopEventArgs {Users=[user],Item=next});await Wait(()=>first.Closed);
            playback.Raise(x=>x.PlaybackStart+=null,new PlaybackProgressEventArgs {Users=[user],Item=movie});await Wait(()=>factory.Connections.Count==2);options.Enabled=false;
            playback.Raise(x=>x.PlaybackProgress+=null,new PlaybackProgressEventArgs {Users=[user],Item=movie});await Wait(()=>factory.Connections.All(x=>x.Closed));
        }finally{await service.StopAsync(default);}
    }
    [Fact] public async Task EncryptedMappingsAreDecryptedOnlyWhenOpeningTheGateway() {
        string folder=Path.Combine(Path.GetTempPath(),"lowfin-service-secret-test-"+Guid.NewGuid().ToString("N"));var tokens=new DiscordTokenProtection(folder);
        var user=new User("encrypted-test","auth","reset"){Id=Guid.NewGuid()};var options=new DiscordOptions {Enabled=true,ApplicationId="123",Users=[new(){JellyfinUserId=user.Id.ToString(),DiscordToken=tokens.Protect(user.Id.ToString(),"fake-secret-token")}]};
        var playback=new Mock<ISessionManager>();var library=new Mock<ILibraryManager>();var users=new Mock<IUserManager>();var factory=new Factory();var clients=new Mock<IHttpClientFactory>();clients.Setup(x=>x.CreateClient(It.IsAny<string>())).Returns(()=>new HttpClient());
        using var service=new DiscordService(playback.Object,library.Object,users.Object,factory,new DiscordArtwork(library.Object,clients.Object),NullLogger<DiscordService>.Instance,()=>options,tokens:tokens);await service.StartAsync(default);
        try {playback.Raise(x=>x.PlaybackStart+=null,new PlaybackProgressEventArgs {Users=[user],Item=new Movie {Id=Guid.NewGuid(),Name="Film"}});await Wait(()=>factory.Connections.Count==1&&factory.Connections.First().Updates.Count==1);Assert.Equal("fake-secret-token",factory.LastToken);Assert.True(DiscordTokenProtection.IsProtected(options.Users[0].DiscordToken));}
        finally{await service.StopAsync(default);Directory.Delete(folder,true);}
    }
    [Fact] public async Task LiveTvProgrammeQueriesAreBoundedAndRefreshAtProgrammeBoundary() {
        var user=new User("epg-test","auth","reset"){Id=Guid.NewGuid()};var clock=new Clock();
        var options=new DiscordOptions {Enabled=true,EnableForLiveTv=true,ApplicationId="123",Users=[new(){JellyfinUserId=user.Id.ToString(),DiscordToken="fake"}]};
        var channel=new LiveTvChannel {Id=Guid.NewGuid(),Name="Station"};int queries=0;
        var programme=new LiveTvProgram {Name="First programme",ChannelId=channel.Id,StartDate=Now.AddMinutes(-10).UtcDateTime,EndDate=Now.AddSeconds(30).UtcDateTime};
        var playback=new Mock<ISessionManager>();var library=new Mock<ILibraryManager>();var users=new Mock<IUserManager>();users.Setup(x=>x.GetUserById(user.Id)).Returns(user);
        library.Setup(x=>x.GetItemList(It.IsAny<InternalItemsQuery>())).Callback<InternalItemsQuery>(q=>{Assert.Equal(1,q.Limit);Assert.Equal(channel.Id,Assert.Single(q.ChannelIds));queries++;}).Returns(()=>new BaseItem[]{programme});
        var factory=new Factory();var clients=new Mock<IHttpClientFactory>();clients.Setup(x=>x.CreateClient(It.IsAny<string>())).Returns(()=>new HttpClient());
        using var service=new DiscordService(playback.Object,library.Object,users.Object,factory,new DiscordArtwork(library.Object,clients.Object),NullLogger<DiscordService>.Instance,()=>options,clock);await service.StartAsync(default);
        try {
            playback.Raise(x=>x.PlaybackStart+=null,new PlaybackProgressEventArgs {Users=[user],Item=channel});await Wait(()=>factory.Connections.Count==1&&factory.Connections.First().Updates.Count==1);var gateway=factory.Connections.First();Assert.Equal(1,queries);
            for(int n=1;n<=4;n++){clock.Now=Now.AddSeconds(n*5);playback.Raise(x=>x.PlaybackProgress+=null,new PlaybackProgressEventArgs {Users=[user],Item=channel,PlaybackPositionTicks=TimeSpan.FromSeconds(n*5).Ticks});await Task.Delay(20);}Assert.Equal(1,queries);Assert.Single(gateway.Updates);
            programme=new LiveTvProgram {Name="Second programme",ChannelId=channel.Id,StartDate=Now.AddSeconds(30).UtcDateTime,EndDate=Now.AddMinutes(20).UtcDateTime};clock.Now=Now.AddSeconds(31);
            playback.Raise(x=>x.PlaybackProgress+=null,new PlaybackProgressEventArgs {Users=[user],Item=channel,PlaybackPositionTicks=TimeSpan.FromSeconds(31).Ticks});await Wait(()=>gateway.Updates.Count==2);Assert.Equal(2,queries);Assert.Equal("Second programme",gateway.Updates.Last()["activities"]![0]!["state"]!.GetValue<string>());
        }finally {await service.StopAsync(default);}
    }
    private sealed class TransientArtworkHandler : HttpMessageHandler {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellation) {
            Calls++;return Task.FromResult(Calls==1?new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable):new HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new StringContent("[{\"external_asset_path\":\"external/recovered\"}]")});
        }
    }
    [Fact] public async Task MissingArtworkRetriesWithoutASeekAndDoesNotResendUnchangedPresence() {
        var user=new User("artwork-test","auth","reset"){Id=Guid.NewGuid()};var clock=new Clock();var options=new DiscordOptions {Enabled=true,ApplicationId="123",FallbackImageUrl="https://images.example.invalid/cover.png",Users=[new(){JellyfinUserId=user.Id.ToString(),DiscordToken="fake"}]};
        var playback=new Mock<ISessionManager>();var library=new Mock<ILibraryManager>();var users=new Mock<IUserManager>();var factory=new Factory();var handler=new TransientArtworkHandler();var clients=new Mock<IHttpClientFactory>();clients.Setup(x=>x.CreateClient(It.IsAny<string>())).Returns(()=>new HttpClient(handler,false));
        using var service=new DiscordService(playback.Object,library.Object,users.Object,factory,new DiscordArtwork(library.Object,clients.Object),NullLogger<DiscordService>.Instance,()=>options,clock);await service.StartAsync(default);var movie=new Movie {Id=Guid.NewGuid(),Name="Film"};
        try {
            playback.Raise(x=>x.PlaybackStart+=null,new PlaybackProgressEventArgs {Users=[user],Item=movie});await Wait(()=>factory.Connections.Count==1&&factory.Connections.First().Updates.Count==1);var gateway=factory.Connections.First();Assert.Null(gateway.Updates.First()["activities"]![0]!["assets"]!["large_image"]);
            clock.Now=Now.AddSeconds(30);playback.Raise(x=>x.PlaybackProgress+=null,new PlaybackProgressEventArgs {Users=[user],Item=movie,PlaybackPositionTicks=TimeSpan.FromSeconds(30).Ticks});await Task.Delay(50);Assert.Single(gateway.Updates);Assert.Equal(1,handler.Calls);
            clock.Now=Now.AddMinutes(2);playback.Raise(x=>x.PlaybackProgress+=null,new PlaybackProgressEventArgs {Users=[user],Item=movie,PlaybackPositionTicks=TimeSpan.FromMinutes(2).Ticks});await Wait(()=>gateway.Updates.Count==2);Assert.Equal("mp:external/recovered",gateway.Updates.Last()["activities"]![0]!["assets"]!["large_image"]!.GetValue<string>());Assert.Equal(2,handler.Calls);
            clock.Now=Now.AddMinutes(18);playback.Raise(x=>x.PlaybackProgress+=null,new PlaybackProgressEventArgs {Users=[user],Item=movie,PlaybackPositionTicks=TimeSpan.FromMinutes(18).Ticks});await Task.Delay(50);Assert.Equal(2,gateway.Updates.Count);Assert.Equal(2,handler.Calls);
        }finally {await service.StopAsync(default);}
    }
    private sealed class ArtworkHandler : HttpMessageHandler {
        public int Calls;public string? Target,Authorization;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellation) {
            Calls++;Target=request.RequestUri!.Host;Authorization=request.Headers.GetValues("Authorization").Single();
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) {Content=new StringContent("[{\"external_asset_path\":\"external/test\"}]")});
        }
    }
    [Fact] public async Task ArtworkIsOptInAndPublicFallbackRegistrationIsCached() {
        var handler=new ArtworkHandler();var clients=new Mock<IHttpClientFactory>();clients.Setup(x=>x.CreateClient(It.IsAny<string>())).Returns(()=>new HttpClient(handler,false));
        var resolver=new DiscordArtwork(new Mock<ILibraryManager>().Object,clients.Object);var movie=new Movie {Id=Guid.NewGuid(),Name="Film"};var options=new DiscordOptions {ApplicationId="123"};
        Assert.Null(await resolver.Resolve(movie,options,"fake-test-token",default));Assert.Equal(0,handler.Calls);
        options.FallbackImageUrl="https://images.example.invalid/cover.jpg";
        Assert.Equal("mp:external/test",await resolver.Resolve(movie,options,"fake-test-token",default));
        Assert.Equal("mp:external/test",await resolver.Resolve(movie,options,"fake-test-token",default));
        Assert.Equal(1,handler.Calls);Assert.Equal("discord.com",handler.Target);Assert.Equal("fake-test-token",handler.Authorization);Assert.False(options.UploadArtwork);
        Assert.Equal("mp:external/test",await resolver.ResolvePublic("https://images.example.invalid/pause.png","123","fake-test-token",default));
        Assert.Equal("mp:external/test",await resolver.ResolvePublic("https://images.example.invalid/pause.png","123","fake-test-token",default));Assert.Equal(2,handler.Calls);
        Assert.Null(await resolver.ResolvePublic("http://images.example.invalid/pause.png","123","fake-test-token",default));Assert.Equal(2,handler.Calls);
    }
    [Fact] public void ManagedLogoCssPreservesOtherCssAndIsIdempotent() {
        const string css="body { color: red; }";var once=DashboardBranding.Merge(css,true);Assert.StartsWith(css,once);Assert.Contains("unicode-range:U+EFFF",once);Assert.Equal(once,DashboardBranding.Merge(once,true));Assert.Equal(css,DashboardBranding.Merge(once,false));
        Assert.NotNull(typeof(Plugin).Assembly.GetManifestResourceStream("Lowfin.Companion.Assets.lowfin-l.woff2"));Assert.NotNull(typeof(Plugin).Assembly.GetManifestResourceStream("Lowfin.Companion.Assets.lowfin-l.svg"));
    }
    [Fact] public void DiscordOptionsRoundTripRetainsUserMappingsAndDisabledDefaults() {
        var options=new DiscordOptions {Users=[new(){JellyfinUserId=Guid.NewGuid().ToString(),DiscordToken="fake-token"}]};
        var serializer=new System.Xml.Serialization.XmlSerializer(typeof(DiscordOptions));using var writer=new StringWriter();serializer.Serialize(writer,options);
        var restored=(DiscordOptions)serializer.Deserialize(new StringReader(writer.ToString()))!;Assert.Single(restored.Users);Assert.Equal("fake-token",restored.Users[0].DiscordToken);Assert.False(restored.Enabled);Assert.False(restored.UploadArtwork);
    }
}
