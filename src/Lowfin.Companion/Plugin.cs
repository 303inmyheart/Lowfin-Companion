using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Lowfin.Companion;

public sealed class PluginConfiguration : BasePluginConfiguration {
    public bool DashboardLogoEnabled {get;set;}=true;
    public Discord.DiscordOptions DiscordRpc {get;set;}=new();
    public bool ImageBadgesEnabled { get; set; }
    public bool ShowDvdBadge { get; set; }=true;
    public bool ShowSurroundBadge { get; set; }=true;
    public bool ShowHdrBadge { get; set; }=true;
    public string[] EnabledCoverTags { get; set; }=ExpandedBadges.Keys.Where(key=>key!="stereo").ToArray();
    public bool ProvidersAllUsers { get; set; }=true;
    public Guid[] ProviderUserIds { get; set; }=[];
    public string OmdbKey { get; set; }="";
    public string MdblistKey { get; set; }="";
    public string TmdbKey { get; set; }="";
    public string TracearrUrl { get; set; }="";
    public string TracearrToken { get; set; }="";
    public string TracearrServerId { get; set; }="";
}
public sealed class Plugin : BasePlugin<PluginConfiguration>,IHasWebPages {
    public Plugin(IApplicationPaths paths,IXmlSerializer serializer) : base(paths,serializer) { DiscordTokens=new(Path.Combine(paths.DataPath,"lowfin-companion","secret-keys"));Instance=this; }
    public static Plugin? Instance { get; private set; }
    public Discord.DiscordTokenProtection DiscordTokens {get;}
    public IEnumerable<PluginPageInfo> GetPages()=>[new() { Name="lowfincompanion",DisplayName="Lowfin Companion",EmbeddedResourcePath="Lowfin.Companion.Configuration.html",EnableInMainMenu=true,MenuIcon=Configuration.DashboardLogoEnabled?"\uefff":"extension" }];
    public event Action? SettingsChanged;
    public override void UpdateConfiguration(BasePluginConfiguration configuration) {
        DiscordTokens.ProtectConfiguration(((PluginConfiguration)configuration).DiscordRpc);
        base.UpdateConfiguration(configuration);SettingsChanged?.Invoke();
    }
    public override string Name => "Lowfin Companion";
    public override Guid Id => new("68abfd24-882d-4937-a104-44d55181b41e");
    public override string Description => "Optional per-user smart completion and independent persistent resume for Lowfin.";
}
public sealed class Registrator : IPluginServiceRegistrator {
    public void RegisterServices(IServiceCollection services,IServerApplicationHost host) {
        services.AddHttpClient();
        services.AddSingleton(provider=>Plugin.Instance!.DiscordTokens);
        services.AddHostedService<Discord.DiscordTokenMigration>();
        services.AddSingleton<Discord.IDiscordGatewayFactory,Discord.DiscordGatewayFactory>();
        services.AddSingleton<Discord.ArtworkUploadCache>();
        services.AddSingleton<Discord.DiscordArtwork>();
        services.AddHostedService<Discord.DiscordService>();
        services.AddHostedService<DashboardBranding>();
        services.AddSingleton(new HostCompatibility(host.ApplicationVersion));
        services.AddSingleton(provider=>new CompanionStore(Path.Combine(provider.GetRequiredService<IApplicationPaths>().DataPath,"lowfin-companion")));
        services.AddSingleton(provider=>new CacheJournal(Path.Combine(provider.GetRequiredService<IApplicationPaths>().DataPath,"lowfin-companion")));
        services.AddSingleton<CacheFeed>();
        services.AddSingleton<IHostedService>(provider=>provider.GetRequiredService<CacheFeed>());
        services.AddSingleton<IBadgeIndexSource,BadgeIndexSource>();
        services.AddSingleton(provider=>new BadgeIndex(Path.Combine(provider.GetRequiredService<IApplicationPaths>().DataPath,"lowfin-companion"),provider.GetRequiredService<IBadgeIndexSource>(),ids=>provider.GetRequiredService<CacheFeed>().NotifyBadgeChanges(ids),log:provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<BadgeIndex>>()));
        services.AddSingleton<IHostedService,BadgeIndexWorker>();
        services.AddSingleton<ProviderGateway>();
        services.AddSingleton<ActionOrder>();
        services.AddSingleton<CompanionService>();
        services.AddSingleton<CompanionEvents>();
        services.AddSingleton<IHostedService>(provider=>provider.GetRequiredService<CompanionEvents>());
    }
}
public sealed record HostCompatibility(Version Version) { public bool Supported => Version>=new Version(12,2); }
