using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Database.Implementations.Entities;
using Lowfin.Companion;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Moq;
using Xunit;

namespace Lowfin.Companion.Tests;

public sealed class ProviderTests {
    private sealed class Handler(Func<HttpRequestMessage,string> response) : HttpMessageHandler {
        public List<string> Requests { get; }=[];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellation) { Requests.Add(request.RequestUri!.ToString()); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content=new StringContent(response(request),Encoding.UTF8,"application/json") }); }
    }
    [Fact] public async Task EpisodeScoresAreFreshUseEpisodeMatchesAndNeverSeriesOrMovieRoutes() {
        var handler=new Handler(request=>request.RequestUri!.Host.Contains("omdb")?"{\"Type\":\"episode\",\"imdbRating\":\"8.6\"}":"{\"tv_results\":[{\"vote_count\":10,\"vote_average\":9.9}],\"tv_episode_results\":[{\"vote_count\":10,\"vote_average\":8.1}]}");
        using var gateway=new ProviderGateway(()=>new() { OmdbKey="key",TmdbKey="key",MdblistKey="key" },handler);
        var episode=new MediaBrowser.Controller.Entities.TV.Episode { Id=Guid.NewGuid() };episode.ProviderIds["Imdb"]="tt987";
        for(int i=0;i<2;i++) { var scores=(await gateway.Ratings(episode,"omdb,tmdb,mdblist",default))["scores"]!;Assert.Equal(8.6,scores["imdb"]!.GetValue<double>());Assert.Equal(8.1,scores["tmdb"]!.GetValue<double>()); }
        Assert.Equal(4,handler.Requests.Count);Assert.DoesNotContain(handler.Requests,x=>x.Contains("mdblist")||x.Contains("/movie/")||x.Contains("/tv/987"));
    }
    [Fact] public async Task EpisodeFallbackUsesSeriesIdsWithSeasonAndEpisodeAndRejectsSeriesResponse() {
        var handler=new Handler(request=>request.RequestUri!.Host.Contains("omdb")?"{\"Type\":\"series\",\"imdbRating\":\"9.9\"}":"{\"vote_count\":10,\"vote_average\":7.3}");
        using var gateway=new ProviderGateway(()=>new() { OmdbKey="key",TmdbKey="key" },handler);
        var series=new MediaBrowser.Controller.Entities.TV.Series();series.ProviderIds["Imdb"]="tt100";series.ProviderIds["Tmdb"]="200";
        var episode=new MediaBrowser.Controller.Entities.TV.Episode { ParentIndexNumber=0,IndexNumber=3 };
        var scores=(await gateway.Ratings(episode,"omdb,tmdb",default,series))["scores"]!;Assert.Null(scores["imdb"]);Assert.Equal(7.3,scores["tmdb"]!.GetValue<double>());
        Assert.Contains(handler.Requests,x=>x.Contains("i=tt100")&&x.Contains("Season=0")&&x.Contains("Episode=3"));Assert.Contains(handler.Requests,x=>x.Contains("/tv/200/season/0/episode/3"));
    }
    [Fact] public void UserAllowListAndStatusNeverExposeSecrets() {
        var first=new User("first","auth","reset"); var other=new User("other","auth","reset"); var config=new PluginConfiguration { OmdbKey="secret-key",TracearrToken="private-token",ProvidersAllUsers=false,ProviderUserIds=[first.Id] };
        using var gateway=new ProviderGateway(()=>config,new Handler(_=>"{}")); Assert.True(gateway.Allowed(first)); Assert.False(gateway.Allowed(other)); string result=JsonSerializer.Serialize(gateway.Status()); Assert.DoesNotContain("secret-key",result); Assert.DoesNotContain("private-token",result); config.ProvidersAllUsers=true; Assert.True(gateway.Allowed(other));
    }
    [Fact] public async Task RatingsAreWhitelistedAndReturnOnlyNormalizedScores() {
        var handler=new Handler(_=>"{\"Response\":\"True\",\"imdbRating\":\"8.2\",\"Metascore\":\"90\",\"secret\":\"should-not-leave-provider\",\"Ratings\":[{\"Source\":\"Rotten Tomatoes\",\"Value\":\"95%\"}]}" );
        using var gateway=new ProviderGateway(()=>new() { OmdbKey="admin-key" },handler); var item=new Movie { Id=Guid.NewGuid() }; item.ProviderIds["Imdb"]="tt123";
        var result=await gateway.Ratings(item,"omdb",CancellationToken.None); Assert.Equal(8.2,result["scores"]!["imdb"]!.GetValue<double>()); Assert.DoesNotContain("should-not-leave-provider",result.ToJsonString()); Assert.DoesNotContain("admin-key",result.ToJsonString()); Assert.Single(handler.Requests);
        await Assert.ThrowsAsync<ArgumentException>(()=>gateway.Ratings(item,"http://untrusted",CancellationToken.None)); Assert.Single(handler.Requests);
    }
    [Fact] public async Task InvalidExternalIdentifiersDoNotMakeRequests() {
        var handler=new Handler(_=>"{}"); using var gateway=new ProviderGateway(()=>new() { OmdbKey="key" },handler); var item=new Movie { Id=Guid.NewGuid() }; item.ProviderIds["Imdb"]="../another-url"; await gateway.Ratings(item,"omdb",CancellationToken.None); Assert.Empty(handler.Requests);
    }
    [Fact] public async Task TracearrMapsOwnJellyfinIdAndCountsOnlyUniqueCompletedPlays() {
        var user=new User("same-name","auth","reset"); var movie=new Movie { Id=Guid.NewGuid() };
        var handler=new Handler(request=>request.RequestUri!.AbsolutePath.EndsWith("/users")?new JsonObject { ["data"]=new JsonArray(new JsonObject { ["id"]="own-user",["accounts"]=new JsonArray(new JsonObject { ["server_id"]="selected-server",["server_type"]="jellyfin",["external_user_id"]=user.Id.ToString() }) }),["meta"]=new JsonObject { ["nextCursor"]=null } }.ToJsonString():new JsonObject { ["data"]=new JsonArray(Play("completed",true,"2026-10-14",movie.Id),Play("completed",true,"2026-10-14",movie.Id),Play("unfinished",false,"2026-10-14",movie.Id),Play("running",true,null,movie.Id),Play("other-item",true,"2026-10-14",Guid.NewGuid())),["meta"]=new JsonObject { ["nextCursor"]=null } }.ToJsonString());
        using var gateway=new ProviderGateway(()=>new() { TracearrUrl="http://tracearr.local",TracearrToken="token",TracearrServerId="selected-server" },handler);
        var result=await gateway.Summary(user,movie,new Mock<ILibraryManager>().Object,CancellationToken.None); Assert.Equal(1,result["count"]!.GetValue<int>()); Assert.Contains(handler.Requests,x=>x.Contains("user_id=own-user")); Assert.DoesNotContain("token",result.ToJsonString());
    }
    private static JsonObject Play(string id,bool watched,string? stopped,Guid media)=>new() { ["id"]=id,["watched"]=watched,["stopped_at"]=stopped,["rating_key"]=media.ToString("N"),["server_id"]="selected-server" };
    [Theory] [InlineData("file:///etc/passwd")] [InlineData("http://user:password@host")] [InlineData("http://host/?token=value")]
    public void UnsafeTracearrUrlsAreRejected(string url)=>Assert.Throws<ArgumentException>(()=>ProviderGateway.TracearrBase(url));
}
