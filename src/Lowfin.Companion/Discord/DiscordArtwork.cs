using System.Globalization;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
namespace Lowfin.Companion.Discord;
/// <summary>Companion configuration adapter around the original Discord artwork pipeline.</summary>
public sealed class DiscordArtwork {
    private readonly ILibraryManager library;
    private readonly CoverService covers;
    private readonly DiscordAssetService assets;
    private readonly ArtworkUploadCache? uploads;
    private readonly ILogger<DiscordArtwork> log;
    public DiscordArtwork(ILibraryManager library,IHttpClientFactory clients,ArtworkUploadCache? uploads=null,ILoggerFactory? logs=null) {
        this.library=library;this.uploads=uploads;logs??=NullLoggerFactory.Instance;
        covers=new(library,clients,logs.CreateLogger<CoverService>());
        assets=new(clients,logs.CreateLogger<DiscordAssetService>());
        log=logs.CreateLogger<DiscordArtwork>();
    }
    public async Task<string?> Resolve(BaseItem item,DiscordOptions options,string token,CancellationToken cancellation) {
        string? url=null;
        if(options.UploadArtwork)url=await UploadLocal(item,options.ArtworkUploadUrl,cancellation).ConfigureAwait(false);
        url??=string.IsNullOrWhiteSpace(options.FallbackImageUrl)?null:options.FallbackImageUrl;
        var asset=await ResolvePublic(url,options.ApplicationId,token,cancellation).ConfigureAwait(false);
        if(options.UploadArtwork||url is not null)log.LogInformation("Discord RPC artwork for item {ItemId}: public URL available: {HasUrl}, Discord asset accepted: {Accepted}.",item.Id,url is not null,asset is not null);
        return asset;
    }
    public Task<string?> ResolvePublic(string? url,string applicationId,string token,CancellationToken cancellation) {
        if(string.IsNullOrWhiteSpace(url))return Task.FromResult<string?>(null);
        if(!url.StartsWith("mp:",StringComparison.Ordinal)&&(!Uri.TryCreate(url,UriKind.Absolute,out var parsed)||parsed.Scheme!="https"||!string.IsNullOrEmpty(parsed.UserInfo)))return Task.FromResult<string?>(null);
        return assets.ResolveAsync(url,applicationId,token,cancellation);
    }
    // Same order as ActivityBuilder.ImageItemCandidates in the supplied source.
    private static IEnumerable<Guid> Candidates(BaseItem item) {
        if(item is Episode episode) {
            if(episode.SeasonId!=Guid.Empty)yield return episode.SeasonId;
            if(episode.SeriesId!=Guid.Empty)yield return episode.SeriesId;
            yield return episode.Id;
        }else if(item is Audio audio) {
            yield return audio.Id;if(audio.AlbumEntity is not null)yield return audio.AlbumEntity.Id;
        }else {
            yield return item.Id;if(item.ParentId!=Guid.Empty)yield return item.ParentId;
        }
    }
    private BaseItem ImageItem(BaseItem item) {
        foreach(var id in Candidates(item)) {
            var candidate=id==item.Id?item:library.GetItemById(id);
            if(candidate?.HasImage(ImageType.Primary,0)==true) {
                log.LogInformation("Discord RPC artwork source for item {ItemId}: {SourceType} {SourceId}.",item.Id,candidate.GetType().Name,candidate.Id);return candidate;
            }
        }
        log.LogWarning("Discord RPC artwork: no primary image found for item {ItemId} or its artwork parents.",item.Id);return item;
    }
    private async Task<string?> UploadLocal(BaseItem item,string uploadUrl,CancellationToken cancellation) {
        try {
            if(!Uri.TryCreate(uploadUrl,UriKind.Absolute,out var endpoint)||endpoint.Scheme!="https"||!string.IsNullOrEmpty(endpoint.UserInfo)) {
                log.LogWarning("Discord RPC artwork: the upload endpoint must be an HTTPS URL without embedded credentials.");return null;
            }
            var source=ImageItem(item);var info=source.GetImageInfo(ImageType.Primary,0);
            string key=string.Format(CultureInfo.InvariantCulture,"{0}|{1}",source.Id.ToString("N",CultureInfo.InvariantCulture),info?.DateModified.Ticks??0);
            if(uploads?.Get(key) is {} cached)return cached;
            var image=await covers.RenderAsync(source.Id,cancellation).ConfigureAwait(false);
            if(image is null) {
                image=await covers.ReadSourceAsync(source.Id,cancellation).ConfigureAwait(false);
                if(image is null)return null;
                log.LogWarning("Discord RPC artwork: uploading the original image for item {ItemId} because square rendering failed.",item.Id);
            }
            var uploaded=await assets.UploadAsync(image,cancellation,uploadUrl).ConfigureAwait(false);
            if(uploaded is not null)uploads?.Set(key,uploaded,TimeSpan.FromMinutes(150));
            return uploaded;
        }catch(OperationCanceledException) when(cancellation.IsCancellationRequested){throw;}
        catch(Exception error){log.LogWarning("Discord RPC artwork upload failed for item {ItemId} ({Kind}).",item.Id,error.GetType().Name);return null;}
    }
}
