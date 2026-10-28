// Adapted from the user-supplied rsbxDev jellyfin-discord-rpc source archive.
using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Lowfin.Companion.Discord;

/// <summary>
/// Turns an ordinary image URL into something Discord accepts as an activity asset.
/// Discord only renders assets uploaded to the application, or external URLs that were
/// registered through the external-assets endpoint, which hands back an "mp:external/..."
/// path. Results are cached because the endpoint is rate limited.
/// </summary>
public sealed class DiscordAssetService
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(6);

    // How long to leave the upload host alone after it refuses one.
    private static readonly TimeSpan UploadCooldown = TimeSpan.FromMinutes(15);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DiscordAssetService> _logger;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    // Set when the upload host pushes back, so a refusal is not answered with more requests.
    private DateTime _uploadBlockedUntil = DateTime.MinValue;

    public DiscordAssetService(IHttpClientFactory httpClientFactory, ILogger<DiscordAssetService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Registers <paramref name="imageUrl"/> with Discord and returns the mp: path, or null
    /// when the image could not be registered.
    /// </summary>
    public async Task<string?> ResolveAsync(
        string? imageUrl,
        string applicationId,
        string token,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(imageUrl) || string.IsNullOrWhiteSpace(applicationId))
        {
            return null;
        }

        if (imageUrl.StartsWith("mp:", StringComparison.Ordinal))
        {
            return imageUrl;
        }

        var key = applicationId + "|" + imageUrl;
        if (_cache.TryGetValue(key, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
        {
            return cached.Value;
        }

        try
        {
            var client = _httpClientFactory.CreateClient(NamedClient.Default);
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"https://discord.com/api/v9/applications/{applicationId}/external-assets");

            request.Headers.TryAddWithoutValidation("Authorization", token);
            request.Headers.TryAddWithoutValidation("User-Agent", DiscordGateway.UserAgent);
            request.Content = JsonContent.Create(new ExternalAssetRequest { Urls = new[] { imageUrl } });

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Discord rejected the artwork {Url}: HTTP {Status}",
                    SafeUrl(imageUrl),
                    (int)response.StatusCode);
                return null;
            }

            var assets = await response.Content
                .ReadFromJsonAsync<ExternalAsset[]>(cancellationToken)
                .ConfigureAwait(false);

            var path = assets is { Length: > 0 } ? assets[0].ExternalAssetPath : null;
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            var resolved = "mp:" + path;
            _cache[key] = new CacheEntry(resolved, DateTime.UtcNow.Add(CacheLifetime));
            return resolved;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to register artwork {Url} with Discord", SafeUrl(imageUrl));
            return null;
        }
    }

    /// <summary>
    /// Uploads image bytes to uguu.se so Discord has a public URL to fetch. Same host the
    /// Navidrome plugin uses; files there live about three hours.
    /// </summary>
    public async Task<string?> UploadAsync(byte[] data, CancellationToken cancellationToken, string uploadUrl = "https://uguu.se/upload")
    {
        if (DateTime.UtcNow < _uploadBlockedUntil)
        {
            return null;
        }

        try
        {
            var client = _httpClientFactory.CreateClient(NamedClient.Default);

            // Announce what the bytes actually are. Declaring the wrong type is what the previous
            // host rejected, and the composed cover is a PNG while raw artwork usually is not.
            var isPng = data.Length > 8
                && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47;

            using var content = new MultipartFormDataContent();
            var file = new ByteArrayContent(data);
            file.Headers.ContentType = new MediaTypeHeaderValue(isPng ? "image/png" : "image/jpeg");
            content.Add(file, "files[]", isPng ? "cover.png" : "cover.jpg");

            using var response = await client
                .PostAsync(new Uri(uploadUrl), content, cancellationToken)
                .ConfigureAwait(false);

            var body = (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim();

            if (!response.IsSuccessStatusCode)
            {
                _uploadBlockedUntil = DateTime.UtcNow.Add(UploadCooldown);
                _logger.LogWarning(
                    "uguu.se refused the upload: HTTP {Status} {Body}. Pausing uploads for {Minutes} minutes.",
                    (int)response.StatusCode,
                    "(response body omitted)",
                    UploadCooldown.TotalMinutes);
                return null;
            }

            var result = JsonSerializer.Deserialize<UguuResponse>(body);
            var url = result?.Files is { Length: > 0 } ? result.Files[0].Url : null;

            if (result?.Success != true || string.IsNullOrWhiteSpace(url))
            {
                _uploadBlockedUntil = DateTime.UtcNow.Add(UploadCooldown);
                _logger.LogWarning(
                    "uguu.se did not return a URL: {Body}. Pausing uploads for {Minutes} minutes.",
                    "(response body omitted)",
                    UploadCooldown.TotalMinutes);
                return null;
            }

            return url;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Artwork upload failed");
            return null;
        }
    }

    private static string SafeUrl(string value)=>Uri.TryCreate(value,UriKind.Absolute,out var url)?url.GetLeftPart(UriPartial.Authority):"(configured image)";

    private sealed class UguuResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("files")]
        public UguuFile[]? Files { get; set; }
    }

    private sealed class UguuFile
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }
    }

    private readonly record struct CacheEntry(string Value, DateTime ExpiresAt);

    private sealed class ExternalAssetRequest
    {
        [JsonPropertyName("urls")]
        public string[] Urls { get; set; } = Array.Empty<string>();
    }

    private sealed class ExternalAsset
    {
        [JsonPropertyName("external_asset_path")]
        public string? ExternalAssetPath { get; set; }
    }
}
