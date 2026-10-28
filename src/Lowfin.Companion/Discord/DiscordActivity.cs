using System.Text.Json.Nodes;
using System.Globalization;
using MediaBrowser.Model.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.LiveTv;
namespace Lowfin.Companion.Discord;
public sealed record Programme(string Title,DateTime Start,DateTime End);
public static class DiscordActivity {
    public static bool Supported(BaseItem item,DiscordOptions options)=>item switch {Episode=>options.EnableForEpisodes,Movie=>options.EnableForMovies,AudioBook or Book=>options.EnableForBooks,Audio=>options.EnableForMusic,LiveTvChannel=>options.EnableForLiveTv,_=>options.EnableForOther};
    private static string? Text(string? text) {if(string.IsNullOrWhiteSpace(text))return null;text=text.Trim();if(text.Length<3)text=text.PadRight(3,'\u200b');return text.Length>128?text[..127]+"…":text;}
    public static JsonObject Build(BaseItem item,long ticks,bool paused,DiscordOptions options,DateTimeOffset now,Programme? programme=null,string? artwork=null,string? pausedArtwork=null) {
        var episode=item as Episode;var audio=item as Audio;
        string genres=item.Genres.Length>0?string.Join(", ",item.Genres):item.ProductionYear?.ToString(CultureInfo.InvariantCulture)??"";
        string artists=audio is null?"":string.Join(", ",audio.Artists.Count>0?audio.Artists:audio.AlbumArtists);
        string parent=episode?.SeriesName??audio?.Album??item.Name;
        var fields=new Dictionary<string,string?> {{"title",item.Name},{"series",episode?.SeriesName},{"season",episode?.ParentIndexNumber?.ToString(CultureInfo.InvariantCulture)},{"episode",episode?.IndexNumber?.ToString(CultureInfo.InvariantCulture)},{"album",audio?.Album},{"artist",artists},{"year",item.ProductionYear?.ToString(CultureInfo.InvariantCulture)},{"genres",genres},{"type",item.GetType().Name}};
        string name=options.NameMode switch {RpcNameMode.Item=>item.Name,RpcNameMode.Parent=>parent,RpcNameMode.Custom=>options.CustomActivityName,_=>"Jellyfin"};
        if(options.NameMode==RpcNameMode.Custom)foreach(var field in fields)name=name.Replace("{"+field.Key+"}",field.Value??"",StringComparison.OrdinalIgnoreCase);
        string? state=episode is not null?(episode.ParentIndexNumber.HasValue&&episode.IndexNumber.HasValue?$"S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00} {episode.Name}":episode.Name):audio is not null?artists:genres;
        if(item is LiveTvChannel)state=programme?.Title??item.Name;
        if(string.IsNullOrWhiteSpace(state)&&paused)state="Paused";
        var activity=new JsonObject { ["application_id"]=options.ApplicationId,["name"]=Text(name)??"Jellyfin",["type"]=audio is null?3:2,["details"]=Text(episode?.SeriesName??item.Name),["state"]=Text(state),["status_display_type"]=Math.Clamp(options.StatusDisplayType,0,2) };
        if(!paused) {
            long start=now.ToUnixTimeMilliseconds()-Math.Max(0,ticks)/10000;long? end=item.RunTimeTicks>0?start+item.RunTimeTicks/10000:null;
            if(item is LiveTvChannel&&programme is not null) {var begin=Utc(programme.Start);var finish=Utc(programme.End);if(begin<=now&&finish>now&&finish>begin) {start=begin.ToUnixTimeMilliseconds();end=finish.ToUnixTimeMilliseconds();}}
            activity["timestamps"]=new JsonObject { ["start"]=start,["end"]=end };
        }
        activity["assets"]=new JsonObject { ["large_image"]=artwork,["large_text"]=Text(parent),["small_image"]=paused?pausedArtwork:null,["small_text"]=paused?"Paused":null };
        if(options.ShowLinkButton) {
            var imdb=item.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Imdb);var tmdb=item.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Tmdb);
            var labels=new JsonArray();var urls=new JsonArray();
            if(imdb?.StartsWith("tt",StringComparison.Ordinal)==true&&imdb.Length>2&&imdb.Skip(2).All(char.IsDigit)){labels.Add("IMDb");urls.Add("https://www.imdb.com/title/"+imdb);}
            if(!string.IsNullOrEmpty(tmdb)&&tmdb.All(char.IsDigit)){labels.Add("TMDB");urls.Add("https://www.themoviedb.org/"+(item is Movie?"movie/":"tv/")+tmdb);}
            if(labels.Count>0){activity["buttons"]=labels;activity["metadata"]=new JsonObject {["button_urls"]=urls};}
        }
        OmitNulls(activity);return activity;
    }
    private static void OmitNulls(JsonObject value) {
        foreach(var key in value.Where(x=>x.Value is null).Select(x=>x.Key).ToArray())value.Remove(key);
        foreach(var child in value.Select(x=>x.Value).OfType<JsonObject>())OmitNulls(child);
    }
    private static DateTimeOffset Utc(DateTime value)=>new(value.Kind==DateTimeKind.Unspecified?DateTime.SpecifyKind(value,DateTimeKind.Utc):value);
}
