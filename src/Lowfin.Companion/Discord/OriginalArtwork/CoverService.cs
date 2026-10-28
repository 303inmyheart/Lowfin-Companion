// Adapted from the user-supplied rsbxDev jellyfin-discord-rpc source archive.
using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Lowfin.Companion.Discord;

/// <summary>
/// Renders the square cover Discord needs. Discord crops the large activity image to a square,
/// so a 2:3 poster loses its top and bottom no matter which size the source is requested in.
/// This is the approach justradical/jellyfin-rpc takes: scale the poster to fit, centre it, and
/// fill the rest of the square with a blurred copy of the same artwork.
/// </summary>
public sealed class CoverService
{
    /// <summary>Edge length of the rendered square.</summary>
    public const int CoverSize = 512;

    // Blur radius as a percentage of the edge length, same default as the reference tool.
    private const float BackgroundBlurPercent = 3f;

    private const int MaxSourceBytes = 16 * 1024 * 1024;
    private const int MaxCacheEntries = 256;

    private readonly ILibraryManager _libraryManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CoverService> _logger;

    // Keyed by item and artwork timestamp, so replacing a cover invalidates the entry by itself.
    private readonly ConcurrentDictionary<string, byte[]> _cache = new();

    public CoverService(
        ILibraryManager libraryManager,
        IHttpClientFactory httpClientFactory,
        ILogger<CoverService> logger)
    {
        _libraryManager = libraryManager;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Returns the item's primary image as a square PNG, or null when the item has no artwork.
    /// </summary>
    public async Task<byte[]?> RenderAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var info = FindImageInfo(itemId);

        if (info is null)
        {
            _logger.LogWarning("No primary image on {ItemId}, cannot render a cover", itemId);
            return null;
        }

        var key = string.Format(
            CultureInfo.InvariantCulture,
            "{0}|{1}",
            itemId.ToString("N", CultureInfo.InvariantCulture),
            info.DateModified.Ticks);

        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var source = await ReadSourceAsync(info, cancellationToken).ConfigureAwait(false);
        if (source is null)
        {
            return null;
        }

        _logger.LogDebug("Rendering a square cover for {ItemId} from {Bytes} bytes", itemId, source.Length);

        byte[]? square;
        try
        {
            square = MakeSquareWithBlur(source);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not render a square cover for {ItemId}", itemId);
            return null;
        }

        if (square is null)
        {
            _logger.LogWarning(
                "SkiaSharp could not decode the artwork of {ItemId} ({Path})",
                itemId,
                info.Path);
            return null;
        }

        // The cache only saves repeated decodes, so dropping it wholesale is cheap enough.
        if (_cache.Count >= MaxCacheEntries)
        {
            _cache.Clear();
        }

        _cache[key] = square;
        return square;
    }

    private ItemImageInfo? FindImageInfo(Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);

        return item?.GetImageInfo(ImageType.Primary, 0);
    }

    /// <summary>
    /// The artwork exactly as Jellyfin stores it. Used when composing fails, so a cover that
    /// cannot be squared is still better than no cover at all.
    /// </summary>
    public async Task<byte[]?> ReadSourceAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var info = FindImageInfo(itemId);

        return info is null ? null : await ReadSourceAsync(info, cancellationToken).ConfigureAwait(false);
    }

    private async Task<byte[]?> ReadSourceAsync(ItemImageInfo info, CancellationToken cancellationToken)
    {
        try
        {
            if (info.IsLocalFile && info.Path is not null && File.Exists(info.Path))
            {
                var length = new FileInfo(info.Path).Length;
                if (length > MaxSourceBytes)
                {
                    _logger.LogWarning("Artwork {Path} is {Bytes} bytes, too large to render", info.Path, length);
                    return null;
                }

                return await File.ReadAllBytesAsync(info.Path, cancellationToken).ConfigureAwait(false);
            }

            if (info.Path is not null && info.Path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                var client = _httpClientFactory.CreateClient(NamedClient.Default);
                return await client.GetByteArrayAsync(info.Path, cancellationToken).ConfigureAwait(false);
            }

            _logger.LogWarning(
                "Artwork is neither a readable file nor a URL (local: {Local}, path: {Path})",
                info.IsLocalFile,
                info.Path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not read the artwork at {Path}", info.Path);
        }

        return null;
    }

    /// <summary>
    /// Draws the artwork centred on a square canvas over a blurred, square-filling copy of
    /// itself. An image that is already square is only rescaled.
    /// </summary>
    public static byte[]? MakeSquareWithBlur(byte[] source, int size = CoverSize)
    {
        using var bitmap = SKBitmap.Decode(source);
        if (bitmap is null || bitmap.Width == 0 || bitmap.Height == 0)
        {
            return null;
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));

        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Black);

        var square = new SKRect(0, 0, size, size);
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);

        if (bitmap.Width != bitmap.Height)
        {
            var blurRadius = size * (BackgroundBlurPercent / 100f);
            using var backdrop = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(blurRadius, blurRadius) };
            canvas.DrawImage(image, square, sampling, backdrop);
        }

        var scale = Math.Min((float)size / bitmap.Width, (float)size / bitmap.Height);
        var width = bitmap.Width * scale;
        var height = bitmap.Height * scale;
        var left = (size - width) / 2f;
        var top = (size - height) / 2f;

        canvas.DrawImage(image, new SKRect(left, top, left + width, top + height), sampling);

        using var rendered = surface.Snapshot();
        using var data = rendered.Encode(SKEncodedImageFormat.Png, 90);

        return data?.ToArray();
    }
}
