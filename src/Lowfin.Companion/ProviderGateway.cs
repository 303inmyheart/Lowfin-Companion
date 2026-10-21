using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Data.Enums;

namespace Lowfin.Companion;

/// <summary>Whitelisted providers and authenticated own-user statistics. Secrets never leave the server.</summary>
public sealed class ProviderGateway : IDisposable {
    private readonly Func<PluginConfiguration> configuration;
    private readonly HttpClient http;
    private readonly SemaphoreSlim concurrency=new(2);
    private readonly Dictionary<string,(DateTimeOffset Stamp,JsonObject Value)> memory=new();
    public ProviderGateway(IApplicationPaths paths) : this(()=>Plugin.Instance?.Configuration??new(),new HttpClientHandler { AllowAutoRedirect=false }) { }
    public ProviderGateway(Func<PluginConfiguration> configuration,HttpMessageHandler handler) { this.configuration=configuration; http=new(handler) { Timeout=TimeSpan.FromSeconds(10) }; }
    public bool Allowed(User user) { var c=configuration(); return c.ProvidersAllUsers||(c.ProviderUserIds??[]).Contains(user.Id); }
    public object Status() { var c=configuration(); return new { omdb=!string.IsNullOrWhiteSpace(c.OmdbKey),mdblist=!string.IsNullOrWhiteSpace(c.MdblistKey),tmdb=!string.IsNullOrWhiteSpace(c.TmdbKey),tracearr=!string.IsNullOrWhiteSpace(c.TracearrUrl)&&!string.IsNullOrWhiteSpace(c.TracearrToken)&&!string.IsNullOrWhiteSpace(c.TracearrServerId) }; }
    private async Task<JsonObject> Get(string url,string? token,CancellationToken cancellation) {
        using var request=new HttpRequestMessage(HttpMethod.Get,url); if(token!=null) request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);
        using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,cancellation).ConfigureAwait(false);
        if(!response.IsSuccessStatusCode) throw new ProviderException();
        await using var stream=await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false); using var output=new MemoryStream(); byte[] block=new byte[8192]; int n;
        while((n=await stream.ReadAsync(block,cancellation).ConfigureAwait(false))>0) { if(output.Length+n>1048576) throw new ProviderException(); output.Write(block,0,n); }
        return JsonNode.Parse(output.ToArray()) as JsonObject??throw new ProviderException();
    }
    private static string Encode(string value)=>Uri.EscapeDataString(value);
    private static double? Number(JsonNode? value) { return double.TryParse(value?.ToString(),NumberStyles.Float,CultureInfo.InvariantCulture,out var number)&&double.IsFinite(number)?number:null; }
    private static void Score(JsonObject scores,string name,JsonNode? value) { var number=Number(value); double max=name is "imdb" or "tmdb"?10:name=="letterboxd"?5:100; if(number>=0&&number<=max) scores[name]=number; }
    public async Task<JsonObject> Ratings(BaseItem item,string providers,CancellationToken cancellation,Series? series=null) {
        var selected=providers.Split(',',StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray(); if(selected.Any(x=>x is not ("omdb" or "mdblist" or "tmdb"))) throw new ArgumentException("Unknown provider.");
        if(item is Episode episode) return await EpisodeRatings(episode,series,selected,cancellation);
        var c=configuration(); var scores=new JsonObject(); var errors=new JsonArray();
        item.ProviderIds.TryGetValue("Imdb",out var imdb); item.ProviderIds.TryGetValue("Tmdb",out var tmdb);
        if(imdb!=null&&!Regex.IsMatch(imdb,"^tt[0-9]+$")) imdb=null; if(tmdb!=null&&!Regex.IsMatch(tmdb,"^[0-9]+$")) tmdb=null;
        await concurrency.WaitAsync(cancellation).ConfigureAwait(false);
        try { foreach(var provider in selected) {
            try {
                JsonObject result; bool movie=item is not Series;
                if(provider=="omdb"&&!string.IsNullOrWhiteSpace(c.OmdbKey)&&imdb!=null) {
                    result=await Get("https://www.omdbapi.com/?apikey="+Encode(c.OmdbKey)+"&i="+imdb,null,cancellation);
                    if(result["Response"]?.ToString()=="False") throw new ProviderException(); Score(scores,"imdb",result["imdbRating"]); Score(scores,"metacritic",result["Metascore"]);
                    if(result["Ratings"] is JsonArray rows) foreach(var row in rows) if(row?["Source"]?.ToString()=="Rotten Tomatoes") Score(scores,"tomatoes",JsonValue.Create(row["Value"]?.ToString().Replace("%","")));
                } else if(provider=="mdblist"&&!string.IsNullOrWhiteSpace(c.MdblistKey)&&(imdb!=null||tmdb!=null)) {
                    result=await Get("https://api.mdblist.com/"+(imdb!=null?"imdb":"tmdb")+"/"+(movie?"movie":"show")+"/"+(imdb??tmdb)+"/?apikey="+Encode(c.MdblistKey),null,cancellation);
                    if(result.ContainsKey("error")||result.ContainsKey("Error")) throw new ProviderException(); Score(scores,"mdblist",result["score"]);
                    if(result["ratings"] is JsonArray rows) foreach(var row in rows) { string name=row?["source"]?.ToString().ToLowerInvariant()??""; name=name switch { "popcorn" or "tomatoes_audience"=>"tomatoesaudience","themoviedb"=>"tmdb",_=>name }; if(new[]{"imdb","tomatoes","tomatoesaudience","metacritic","tmdb","trakt","letterboxd"}.Contains(name)) Score(scores,name,row?["value"]); }
                } else if(provider=="tmdb"&&!string.IsNullOrWhiteSpace(c.TmdbKey)&&(tmdb!=null||imdb!=null)) {
                    string key=c.TmdbKey,token=key.Contains('.')?key:""; string suffix=token.Length>0?"":"api_key="+Encode(key);
                    string path=tmdb!=null?(movie?"movie/":"tv/")+tmdb:"find/"+imdb;
                    result=await Get("https://api.themoviedb.org/3/"+path+"?"+suffix+(tmdb==null?"&external_source=imdb_id":""),token.Length>0?token:null,cancellation);
                    if(tmdb==null) result=(result[movie?"movie_results":"tv_results"] as JsonArray)?.FirstOrDefault() as JsonObject??new();
                    if(Number(result["vote_count"])>0) Score(scores,"tmdb",result["vote_average"]);
                }
            } catch(Exception error) when(error is ProviderException or HttpRequestException or TaskCanceledException or JsonException or FormatException) { if(cancellation.IsCancellationRequested) throw; errors.Add(provider); }
        } } finally { concurrency.Release(); }
        return new() { ["scores"]=scores,["errors"]=errors };
    }
    private async Task<JsonObject> EpisodeRatings(Episode episode,Series? series,string[] selected,CancellationToken cancellation) {
        var c=configuration();var scores=new JsonObject();var errors=new JsonArray();
        episode.ProviderIds.TryGetValue("Imdb",out var imdb);if(imdb==null||!Regex.IsMatch(imdb,"^tt[0-9]+$")) imdb=null;
        string? seriesImdb=null,seriesTmdb=null;series?.ProviderIds.TryGetValue("Imdb",out seriesImdb);series?.ProviderIds.TryGetValue("Tmdb",out seriesTmdb);
        if(seriesImdb==null||!Regex.IsMatch(seriesImdb,"^tt[0-9]+$")) seriesImdb=null;
        if(seriesTmdb==null||!Regex.IsMatch(seriesTmdb,"^[0-9]+$")) seriesTmdb=null;
        bool coordinates=episode.ParentIndexNumber>=0&&episode.IndexNumber>0;
        await concurrency.WaitAsync(cancellation);
        try { foreach(string provider in selected) try {
            if(provider=="omdb"&&!string.IsNullOrWhiteSpace(c.OmdbKey)&&(imdb!=null||seriesImdb!=null&&coordinates)) {
                string suffix=imdb==null?"&Season="+episode.ParentIndexNumber+"&Episode="+episode.IndexNumber:"";
                var result=await Get("https://www.omdbapi.com/?apikey="+Encode(c.OmdbKey)+"&i="+(imdb??seriesImdb)+"&type=episode"+suffix,null,cancellation);
                if(result["Response"]?.ToString()=="False") throw new ProviderException();
                if(string.Equals(result["Type"]?.ToString(),"episode",StringComparison.OrdinalIgnoreCase)) Score(scores,"imdb",result["imdbRating"]);
            } else if(provider=="tmdb"&&!string.IsNullOrWhiteSpace(c.TmdbKey)&&(imdb!=null||seriesTmdb!=null&&coordinates)) {
                string key=c.TmdbKey,token=key.Contains('.')?key:"",suffix=token.Length>0?"":"api_key="+Encode(key);
                string path=imdb!=null?"find/"+imdb:"tv/"+seriesTmdb+"/season/"+episode.ParentIndexNumber+"/episode/"+episode.IndexNumber;
                var result=await Get("https://api.themoviedb.org/3/"+path+"?"+suffix+(imdb!=null?"&external_source=imdb_id":""),token.Length>0?token:null,cancellation);
                if(imdb!=null) result=(result["tv_episode_results"] as JsonArray)?.FirstOrDefault() as JsonObject??new();
                if(Number(result["vote_count"])>0) Score(scores,"tmdb",result["vote_average"]);
            }
        } catch(Exception error) when(error is ProviderException or HttpRequestException or TaskCanceledException or JsonException or FormatException) { if(cancellation.IsCancellationRequested) throw;errors.Add(provider); } }
        finally { concurrency.Release(); }
        return new() { ["scores"]=scores,["errors"]=errors };
    }
    public static string TracearrBase(string value) {
        if(!Uri.TryCreate(value.Trim().TrimEnd('/'),UriKind.Absolute,out var uri)||uri.Scheme is not ("http" or "https")||uri.UserInfo.Length>0||uri.Query.Length>0||uri.Fragment.Length>0) throw new ArgumentException("Invalid Tracearr URL."); return uri.ToString().TrimEnd('/')+"/api/v2/public";
    }
    private Task<JsonObject> Trace(string path,PluginConfiguration c,CancellationToken cancellation) { if(string.IsNullOrWhiteSpace(c.TracearrToken)) throw new ProviderException(); return Get(TracearrBase(c.TracearrUrl)+path,c.TracearrToken,cancellation); }
    public async Task<JsonObject> Servers(CancellationToken cancellation) { var result=await Trace("/servers",configuration(),cancellation); var rows=result["data"] as JsonArray??throw new ProviderException(); var output=new JsonArray(); foreach(var row in rows) if(row?["server_type"]?.ToString()=="jellyfin"&&row["historical"]?.ToString()!="true") output.Add(new JsonObject { ["server_id"]=row["server_id"]?.ToString(),["name"]=row["name"]?.ToString() }); return new() { ["data"]=output }; }
    private static string Next(JsonObject response) { if(response["meta"] is not JsonObject meta||!meta.ContainsKey("nextCursor")) throw new ProviderException(); return meta["nextCursor"]?.ToString()??""; }
    public async Task<JsonObject> Summary(User user,BaseItem item,ILibraryManager library,CancellationToken cancellation) {
        if(!Allowed(user)) throw new System.Security.SecurityException();
        var c=configuration(); if(string.IsNullOrWhiteSpace(c.TracearrServerId)) throw new ProviderException(); string cache=user.Id+":"+item.Id+":"+c.TracearrUrl+":"+c.TracearrServerId+":"+Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(c.TracearrToken)));
        lock(memory) if(memory.TryGetValue(cache,out var saved)&&DateTimeOffset.UtcNow-saved.Stamp<TimeSpan.FromMinutes(1)) return (JsonObject)saved.Value.DeepClone();
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(TimeSpan.FromSeconds(30)); var token=deadline.Token;
        await concurrency.WaitAsync(token).ConfigureAwait(false);
        try {
            string? identity=null,cursor=""; var cursors=new HashSet<string>(); var matches=new HashSet<string>();
            for(int page=0;page<100;page++) {
                var response=await Trace("/users?pageSize=100"+(cursor.Length>0?"&cursor="+Encode(cursor):""),c,token); var rows=response["data"] as JsonArray??throw new ProviderException();
                foreach(var row in rows) if(row?["accounts"] is JsonArray accounts) foreach(var account in accounts) if(account?["server_id"]?.ToString()==c.TracearrServerId&&account["server_type"]?.ToString()=="jellyfin"&&Guid.TryParse(account["external_user_id"]?.ToString(),out var external)&&external==user.Id) matches.Add(row["id"]?.ToString()??"");
                cursor=Next(response); if(cursor.Length==0) { if(matches.Count!=1||matches.Contains("")) throw new ProviderException(); identity=matches.Single(); break; } if(!cursors.Add(cursor)) throw new ProviderException();
            }
            if(identity==null) throw new ProviderException(); bool series=item is Series; var expected=new HashSet<Guid>();
            if(series) { var result=library.GetItemsResult(new InternalItemsQuery(user) { ParentId=item.Id,Recursive=true,IncludeItemTypes=[BaseItemKind.Episode],EnableTotalRecordCount=false }); foreach(var episode in result.Items) expected.Add(episode.Id); if(expected.Count==0) throw new ProviderException(); }
            cursor=""; cursors.Clear(); var plays=new HashSet<string>(); var counts=new Dictionary<Guid,int>(); bool complete=false;
            for(int page=0;page<1000;page++) {
                string path="/history?server_id="+Encode(c.TracearrServerId)+"&user_id="+Encode(identity)+"&watched=true&pageSize=100&media_type="+(series||item is Episode?"episode":"movie")+(series?"":"&rating_key="+item.Id.ToString("N"))+(cursor.Length==0?"":"&cursor="+Encode(cursor));
                var response=await Trace(path,c,token); var rows=response["data"] as JsonArray??throw new ProviderException();
                foreach(var row in rows) {
                    if(row?["watched"]?.ToString()!="true"||string.IsNullOrEmpty(row["stopped_at"]?.ToString())||row["server_id"]?.ToString()!=c.TracearrServerId||!Guid.TryParse(row["rating_key"]?.ToString(),out var id)||(!series&&id!=item.Id)||(series&&!expected.Contains(id))) continue;
                    string play=row["id"]?.ToString()??""; if(play.Length>0&&plays.Add(play)) counts[id]=counts.GetValueOrDefault(id)+1;
                }
                cursor=Next(response); if(cursor.Length==0) { complete=true; break; } if(!cursors.Add(cursor)) throw new ProviderException();
            }
            if(!complete) throw new ProviderException(); int count=series?expected.Min(id=>counts.GetValueOrDefault(id)):plays.Count;
            var summary=new JsonObject { ["count"]=count,["series"]=series,["episodes"]=expected.Count };
            lock(memory) { if(memory.Count>=128) memory.Clear(); memory[cache]=(DateTimeOffset.UtcNow,summary); } return summary;
        } finally { concurrency.Release(); }
    }
    public void Dispose() { http.Dispose(); concurrency.Dispose(); }
}
public sealed class ProviderException : Exception { public ProviderException() : base("Provider unavailable.") { } }
