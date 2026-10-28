using MediaBrowser.Controller.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Branding;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
namespace Lowfin.Companion;
/// <summary>Use Jellyfin's supported branding CSS to add a single private-use icon glyph.</summary>
public sealed class DashboardBranding(IServerConfigurationManager configuration,ILogger<DashboardBranding> log) : IHostedService {
    public const string Begin="/* Lowfin Companion mark: begin */",End="/* Lowfin Companion mark: end */";
    public static string Merge(string? existing,bool enabled) {
        string css=existing??"";int start=css.IndexOf(Begin,StringComparison.Ordinal),finish=css.IndexOf(End,StringComparison.Ordinal);
        if(start>=0&&finish>=start)css=css.Remove(start,finish+End.Length-start);
        if(!enabled)return css.TrimEnd();
        return css.TrimEnd()+"\n"+Begin+"\n@font-face{font-family:'Material Icons';font-style:normal;font-weight:400;src:url('../Lowfin/Companion/v1/dashboard/mark.woff2') format('woff2');unicode-range:U+EFFF;font-display:swap;}\n"+End;
    }
    private void Update() {
        try {var options=(BrandingOptions)configuration.GetConfiguration("branding");var next=Merge(options.CustomCss,Plugin.Instance?.Configuration.DashboardLogoEnabled==true);if(next!=options.CustomCss){options.CustomCss=next;configuration.SaveConfiguration("branding",options);}}
        catch(Exception error){log.LogWarning(error,"Could not register the Lowfin dashboard mark; ordinary plugin settings remain available.");}
    }
    public Task StartAsync(CancellationToken cancellation) {Update();if(Plugin.Instance is {} plugin)plugin.SettingsChanged+=Update;return Task.CompletedTask;}
    public Task StopAsync(CancellationToken cancellation) {if(Plugin.Instance is {} plugin)plugin.SettingsChanged-=Update;try {var options=(BrandingOptions)configuration.GetConfiguration("branding");var next=Merge(options.CustomCss,false);if(next!=options.CustomCss){options.CustomCss=next;configuration.SaveConfiguration("branding",options);}}catch(Exception error){log.LogDebug(error,"Could not remove the managed Lowfin CSS block during shutdown.");}return Task.CompletedTask;}
}
[ApiController,Route("Lowfin/Companion/v1/dashboard")]
public sealed class DashboardAssetsController : ControllerBase {
    [AllowAnonymous,HttpGet("mark.woff2")]
    public IActionResult Mark() {Response.Headers.CacheControl="public,max-age=86400";return File(typeof(Plugin).Assembly.GetManifestResourceStream("Lowfin.Companion.Assets.lowfin-l.woff2")!,"font/woff2");}
    [AllowAnonymous,HttpGet("mark.svg")]
    public IActionResult Svg()=>File(typeof(Plugin).Assembly.GetManifestResourceStream("Lowfin.Companion.Assets.lowfin-l.svg")!,"image/svg+xml");
}
