// Adapted from the user-supplied rsbxDev jellyfin-discord-rpc source archive.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Lowfin.Companion.Discord;

/// <summary>
/// Remembers where an artwork was uploaded to. The key is the item the cover belongs to, so a
/// whole season is uploaded once instead of once per episode. jellyfin-rpc keeps the same list;
/// without it the upload host starts refusing the traffic.
/// </summary>
public sealed class ArtworkUploadCache
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly ILogger<ArtworkUploadCache> _logger;
    private readonly string _file;
    private readonly object _fileLock = new();

    private bool _loaded;

    public ArtworkUploadCache(IApplicationPaths applicationPaths, ILogger<ArtworkUploadCache> logger)
    {
        _logger = logger;
        _file = Path.Combine(applicationPaths.CachePath, "lowfin-companion-discord-uploads.json");
    }

    /// <summary>Path of the file the cache lives in, so it can be pointed at or deleted.</summary>
    public string FilePath => _file;

    /// <summary>The still valid URL for this key, or null.</summary>
    public string? Get(string key)
    {
        EnsureLoaded();

        if (_entries.TryGetValue(key, out var entry) && entry.ExpiresAt > DateTime.UtcNow)
        {
            return entry.Url;
        }

        return null;
    }

    public void Set(string key, string url, TimeSpan lifetime)
    {
        EnsureLoaded();

        _entries[key] = new Entry(url, DateTime.UtcNow.Add(lifetime));
        Save();
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        lock (_fileLock)
        {
            if (_loaded)
            {
                return;
            }

            _loaded = true;

            try
            {
                if (!File.Exists(_file))
                {
                    return;
                }

                var stored = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_file));
                if (stored is null)
                {
                    return;
                }

                var now = DateTime.UtcNow;
                foreach (var pair in stored.Where(pair => pair.Value.ExpiresAt > now))
                {
                    _entries[pair.Key] = pair.Value;
                }

                _logger.LogInformation("Loaded {Count} cached artwork uploads from {File}", _entries.Count, _file);
            }
            catch (Exception ex)
            {
                // A damaged cache costs an upload, nothing more.
                _logger.LogWarning(ex, "Could not read the artwork upload cache at {File}", _file);
            }
        }
    }

    private void Save()
    {
        lock (_fileLock)
        {
            try
            {
                var now = DateTime.UtcNow;

                // Expired entries are dropped on the way out rather than on a timer.
                foreach (var pair in _entries.Where(pair => pair.Value.ExpiresAt <= now))
                {
                    _entries.TryRemove(pair.Key, out _);
                }

                File.WriteAllText(_file, JsonSerializer.Serialize(_entries));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not write the artwork upload cache to {File}", _file);
            }
        }
    }

    public sealed record Entry(string Url, DateTime ExpiresAt);
}
