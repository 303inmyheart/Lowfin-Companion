# Changelog

## 0.8.0

- Fix the stored-loudness status wire format: explicitly serialize lowercase field names instead of depending on Jellyfin's JSON naming policy. Accept both current and legacy PascalCase fields in the dashboard so a completed inventory no longer appears to count forever.
- Refresh loudness status automatically while stored profiles are being counted or audio measurement is running. Stop polling when ready/idle, when leaving the page or hiding the browser tab; ignore stale responses and avoid concurrent status requests.
- Keep stored-profile inventory responsive on slow storage and tolerate incomplete JSON rows without discarding healthy measurements. Surface background inventory failures instead of leaving a permanent counting indicator.
- Separate persisted loudness-profile totals from per-run counters in the dashboard. Inventory saved profiles once in the background after restart and maintain totals as profiles are written or removed; do not display a reset run counter as the saved total.
- Reuse unchanged cover facts and per-user series/season summaries during daily checks. Batch native user-scoped episode queries, update only affected parents, preserve events arriving during a scan, and expose reused/recalculated counters. Revalidate old index entries once when revision information is missing.
- Coalesce repeated metadata notifications into bounded delivery pages and suppress already delivered unchanged revisions. Preserve image updates, computed cover-tag changes, removals, cursor resets and the durable change history.
- Page loudness discovery without redundant item lookups, reuse native stereo measurements for the identical two-channel downmix, skip duplicate snapshot writes, and preserve measurements while a media share is unavailable. Ordinary folder bookkeeping no longer queues a whole-library audio check.
- Add a real Jellyfin scheduled loudness-measurement task, started automatically by new/updated local media and available for manual runs/cancellation. Reconcile daily, skip unchanged audio, measure each embedded track/media version separately and persist completed profiles across restarts.
- Add a default-off dashboard switch, measurement status, bounded single-process FFmpeg analysis and an authenticated, visibility-checked measurement API. Do not measure Live TV or change media files, playback reporting or global server settings.
- Return the plugin version from its assembly rather than an outdated capability literal.

## 0.7.2

- Add a default-on background/AFK mode for Discord presence so playback does not continually report an active desktop session. Include the initial presence during gateway identification and keep inactivity timestamps stable. Default new configurations to Online, preserve saved status choices, and apply presence-policy changes on the next playback event without waiting for a seek or artwork refresh. Notification delivery remains controlled by Discord.

- Verify Online, Idle, Do Not Disturb and Invisible mappings through dashboard load/save and real loopback gateway messages, with background mode both enabled and disabled. Clear stopped presence without advertising an active session.

## 0.7.1

- Increase retained library changes from 4,096 to 250,000. Append checksummed durable records, checkpoint periodically, migrate the existing snapshot without replacing its cursor epoch, recover incomplete unacknowledged tails and return bounded pages without copying the complete history for each request.
- Increase the short-lived event queue to 32,768 unique pending items; retain explicit gap detection for overflow, restarts and access changes.

## 0.7.0

- Build with neutral deterministic debug paths and anonymize public test/documentation data; preserve plugin identifiers, state formats and versions.

- Use neutral Discord artwork documentation and remove personal references and private repository links.

- Add a dashboard sidebar entry with the original Lowfin L and an optional, single-glyph branding font. Preserve existing custom CSS.
- Reorganize English dashboard settings into overview, cover tags/index, rating providers, Tracearr and Discord sections.
- Add optional per-user Discord presence for movies, episodes, music, books and live TV, including current programme names and valid EPG time bars. Add configurable templates, throttling, cleanup, link buttons and optional artwork uploads. Configure mappings directly; no standalone import.
- Improve Discord transport compatibility: unversioned discovered gateway URL, client headers, fragmented READY messages up to 32 MiB, queued presence replay, explicit close-code logs and terminal authentication failure handling. Omit null optional fields, use zero for presence `since`, resolve paused artwork, and send pause/resume transitions immediately.
- Integrate cover rendering, a persistent upload cache and Discord asset registration. Restore local/remote sources, 512px PNG composition, raw-image fallback, correct multipart MIME types and upload reuse across episodes/restarts. Add bounded missing-image retries and diagnostic stages.
- Encrypt Discord tokens at rest using a persistent, user-bound Data Protection key ring. Migrate old mappings, retain tokens when fields stay blank, avoid plaintext token echo, preserve encrypted values when keys are unavailable and document backup/security limits.
- Keep Discord integration and artwork uploads off by default; preserve ordinary Jellyfin playback/completion reporting.
- Remove the upper Jellyfin minor-version gate that disabled capabilities and cover tags on 12.3; retain the 12.2 minimum. Test against both 12.2 and 12.3 API packages.
- Generate repository and manual ZIPs, meta.json, release checksums and the update manifest together. Keep the stable GUID/feed URL and previous release entries.
- Verify 111 .NET tests against each of the 12.2 and 12.3 API packages, dashboard behavior, feed generation and an isolated Jellyfin 12.2 server load. Discord transport is exercised against a loopback WebSocket server; no real Discord account was connected.

## 0.6.0

- Add all JellyTag detection categories as named Lowfin-native cover facts: resolutions, HDR variants, 3D, video/audio codecs, channel layouts and language/subtitle codes.
- Add English dashboard controls for individual categories; stereo stays off by default. Preserve legacy tag masks and existing client compatibility.
- Persist named facts in the background index, update affected containers and invalidate clients when labels change. Upgrading an old index does not flood the metadata journal.
- Derive series/season labels from user-visible episodes; use strict DVD majority and highest non-DVD resolution.

## 0.5.1

- Cover-index progress across scan, per-user summaries and persistence; show progress while waiting for an active background rebuild.
- Start/completion/cancellation/error logs and a diagnostic phase/counter heartbeat every 15 seconds.
- Dashboard index phase/counters; 78 .NET tests passed.
- New manual packages allow repository auto-updates; older packages with autoUpdate disabled need the documented one-time setting change.
- Separate source repository with English/German documentation, local builds, release uploads and a public Jellyfin update manifest. No GitHub Actions build is required.

## 0.5.0

- Persistent cover-tag index, event updates and 24-hour reconciliation.
- DVD majority for series/seasons and optional HDR facts gated by local HDR playback.

## 0.4.1

- Episode-specific external ratings without episode-rating caching.
- DVD series/season majority support.

## 0.4.0

- Optional frame-relative DVD/5.1 tags; original artwork remains untouched.

## 0.3.0

- Server-side provider credentials with selected-user access.

## 0.2.0

- Incremental metadata/artwork change journal for completed Lowfin precaches.

## 0.1.1

- Explicit rule opt-in, library-scoped resume and Jellyfin home exclusions.

## 0.1.0

- Opt-in Smart Completion and Persistent Resume with per-user rules and normal Jellyfin authentication/playback events.
