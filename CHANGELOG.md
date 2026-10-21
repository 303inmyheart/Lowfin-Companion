# Changelog

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
