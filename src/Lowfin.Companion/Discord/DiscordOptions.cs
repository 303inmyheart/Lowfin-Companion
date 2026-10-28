using System.Xml.Serialization;
namespace Lowfin.Companion.Discord;
public enum RpcNameMode { Default,Item,Parent,Custom }
public sealed class DiscordUser {
    public string JellyfinUserId {get;set;}="";
    public string DiscordToken {get;set;}="";
    public bool Enabled {get;set;}=true;
}
[XmlRoot("PluginConfiguration")]
public sealed class DiscordOptions {
    public bool Enabled {get;set;}
    public string ApplicationId {get;set;}="";
    [XmlArrayItem("UserTokenMapping")] public DiscordUser[] Users {get;set;}=[];
    public string OnlineStatus {get;set;}="dnd";
    public RpcNameMode NameMode {get;set;}
    public string CustomActivityName {get;set;}="{title}";
    public int StatusDisplayType {get;set;}=1;
    public bool ShowPaused {get;set;}=true;
    public string FallbackImageUrl {get;set;}="";
    public string PauseImageUrl {get;set;}="";
    public bool EnableForMovies {get;set;}=true;
    public bool EnableForEpisodes {get;set;}=true;
    public bool EnableForMusic {get;set;}=true;
    public bool EnableForBooks {get;set;}=true;
    public bool EnableForLiveTv {get;set;}
    public bool EnableForOther {get;set;}
    public int MinUpdateIntervalSeconds {get;set;}=15;
    public int IdleTimeoutSeconds {get;set;}=120;
    public bool ShowLinkButton {get;set;}
    public bool UploadArtwork {get;set;}
    public string ArtworkUploadUrl {get;set;}="https://uguu.se/upload";
    public DiscordUser? User(Guid id)=>Users.FirstOrDefault(x=>x.Enabled&&Guid.TryParse(x.JellyfinUserId,out var parsed)&&parsed==id&&!string.IsNullOrWhiteSpace(x.DiscordToken));
}
