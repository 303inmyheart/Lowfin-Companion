# Lowfin Companion

**Lowfin-specific code, documentation and design are 100% AI-generated. This release also includes adapted Discord artwork components.**

> **Personally tested:** As the project owner, I have extensively tested all features myself.

<img src="assets/lowfin-companion.png" alt="Lowfin Companion" width="260" />

Optional server-side companion for [Lowfin](https://github.com/303inmyheart/Lowfin), designed for **Jellyfin 12.2 or newer**. Current plugin version: **0.7.0**. The Android TV client works independently when the plugin is absent.

[Deutsche Anleitung](README.de.md)

[Changelog](CHANGELOG.md) · [Releases](https://github.com/303inmyheart/Lowfin-Companion/releases)

## Features

- **Smart Completion:** per-user, per-movie/episode rules: Jellyfin default, custom percentage, remaining time or never auto-complete. Existing Jellyfin global thresholds stay unchanged. Only an explicitly selected rule or enabled long-video rule activates Companion processing.
- **Persistent Resume:** independent resume positions, playback sequence/order checks, and fences for manual watched/reset actions. The same Jellyfin user's selected rules apply across Lowfin devices.
- **Incremental library cache:** a persistent change journal lets Lowfin update changed metadata and artwork instead of rebuilding the completed library cache. Library permissions and resume exclusions remain respected.
- **Server-side provider configuration:** OMDb, MDBList, TMDB and Tracearr credentials in the Jellyfin plugin dashboard, with all-user or selected-user access. Lowfin retains its provider/display toggles and local rating-cache setting.
- **Cover tags:** optional DVD/SD, 5.1 and HDR facts. Lowfin draws the labels in the visible artwork frame; originals and other clients' images stay unchanged. Series/season DVD majority uses available episodes. HDR tags additionally require HDR playback enabled in that Lowfin client.
- **Persistent tag index:** event-driven changes plus a 24-hour reconciliation task. The index includes progress, phase counters and 15-second diagnostic logs; search the Jellyfin log for `Lowfin cover index`.
- **Discord Rich Presence (0.7.0):** optional per-user mappings, encrypted token storage, the Discord artwork pipeline and live-TV channel/programme timing. Disabled by default; configure it in its own dashboard section. See the setup and backup notes below.
- **Dashboard navigation:** a sidebar entry with the original Lowfin L; grouped settings for completion, tags/index, provider access, Tracearr and Discord.

## Compatibility and limits

Requires **Jellyfin 12.2 or newer** and .NET 10 for development. There is no upper version gate. The plugin is built against the minimum 12.2 API and its tests also run against 12.3 packages; future server releases can still introduce breaking APIs. A full isolated server load is tested on 12.2; this does not guarantee compatibility with every future release. Normal Jellyfin authentication and playback reporting remain in use. Other Jellyfin clients and tracking systems retain their existing completion rules. Lost historical positions cannot be reconstructed.

Movie/series external ratings follow Lowfin's existing cache settings. Episode-specific ratings are requested on demand and are not cached. This plugin does not change your video files or global Jellyfin completion settings.

## Install a locally built package

Download the locally built package from [GitHub releases](https://github.com/303inmyheart/Lowfin-Companion/releases). This repository has no GitHub Actions workflow; releases are built and uploaded locally.

1. Download the `-manual.zip` release asset, or build and package locally (see below). The unsuffixed ZIP is the flat package used by Jellyfin’s repository installer.
2. Stop Jellyfin.
3. Extract `Lowfin-Companion-0.7.0-manual.zip` and copy its entire `Lowfin.Companion_0.7.0.0` directory into Jellyfin's plugin directory. DLL, `meta.json` and image must be directly inside that versioned directory.
4. Move the previous Companion plugin directory out of `plugins` as a backup, so only one version loads. Preserve the separate `lowfin-companion` data directory.
5. Start Jellyfin and open Dashboard → Plugins → Lowfin Companion. Configure providers/user access and optional tags there, then reopen Lowfin.

The plugin directory is often `/var/lib/jellyfin/plugins` on Linux or `/config/plugins` in Docker; use your server's actual configured path. Server configuration/data and API keys do **not** belong in this Git repository.

## Build and test locally

Prerequisites: **.NET SDK 10**, Python 3.10+, and network access to NuGet for the first restore. No Android SDK or GitHub Actions minutes are needed.

```bash
dotnet test tests/Lowfin.Companion.Tests/Lowfin.Companion.Tests.csproj -c Release
dotnet build src/Lowfin.Companion/Lowfin.Companion.csproj -c Release
python3 tools/package.py
```

Or run `bash tools/build.sh`. Set `DOTNET=/absolute/path/to/dotnet` if the SDK is not on PATH. Packaging writes the manual-install ZIP, versioned plugin folder, `meta.json`, artwork and checksums under ignored `artifacts/`. The test suite covers long videos, persistent resume, races, explicit user actions, permissions, provider behavior, tag detection/indexing and index progress.

## Install and update through Jellyfin

Add this repository URL under **Jellyfin Dashboard → Plugins → Repositories**:

```text
https://raw.githubusercontent.com/303inmyheart/Lowfin-Companion/main/manifest.json
```

Then open the plugin catalog, select Lowfin Companion and install it. Jellyfin's plugin-update task can check the same feed for future compatible releases. Restart Jellyfin after installation or an update. If an older manually installed package has `autoUpdate: false` in its `meta.json`, automatic checks remain disabled for that installation. With Jellyfin stopped, change that flag to `true` (preserving all other metadata) or replace it with the current manual package before relying on scheduled updates. The manifest uses the stable Companion GUID and Jellyfin 12.2 ABI. The ordinary GitHub project page is not a Jellyfin repository URL.

The local tool prepares a standard feed for a future approved HTTP(S) hosting directory, without uploading anything:

```bash
python3 -m unittest discover -s tools -p 'test_*.py'
python3 tools/make_repository.py \
  --base-url https://YOUR-HOST/lowfin-companion/ \
  --output artifacts/feed
```

The example command illustrates custom hosting. For the published feed, sourceUrl points to the corresponding public GitHub release asset. The output contains:

- `manifest.json` with stable plugin GUID, version, Jellyfin ABI, package URL and Jellyfin-required MD5 checksum;
- a **flat** install ZIP with the DLL at its root, matching Jellyfin's installer;
- plugin image and an additional SHA-256 checksum.

The normal manual-install ZIP uses a versioned outer directory; the feed ZIP deliberately does not. Run the generator in the same output directory to retain previous manifest versions and their packages. For a future update, build locally, regenerate the feed, upload its files to the approved host, then let Jellyfin's configured plugin-update task check it. New plugin versions need a Jellyfin restart to load. For this repository, the approved host is GitHub: commit the generated manifest/image and upload the matching ZIP to its release. Keep the sourceUrl and checksum matched to the exact uploaded bytes.

## Data and diagnostics

Persistent plugin data lives below `<Jellyfin data directory>/lowfin-companion/`, including per-user completion/resume files, the change journal and `badge-index.json`. Include this directory and the Jellyfin plugin configuration in server backups. Uninstalling the plugin does not automatically erase these files.

Index status is available in the admin-only plugin dashboard/status endpoint. Existing snapshots stay usable during rebuilding, cancellation or failures. Provider secrets are admin-configured and are not returned to ordinary Lowfin users.

## Source layout

- `src/Lowfin.Companion/`: native Jellyfin plugin, API, state, providers, journal and index.
- `tests/Lowfin.Companion.Tests/`: .NET regression tests.
- `tools/`: local build, manual packaging and optional feed preparation.
- `assets/`: plugin artwork and design notes.

Lowfin's original work is licensed under [GNU GPL v2](LICENSE). For adapted components, see [third-party notices and source provenance](THIRD_PARTY.md).

## Extended cover tags (0.6.0)

The dashboard can select DVD/SD, 720p, 1080p, 4K, HDR/HDR10/HDR10+/Dolby Vision/HLG, 3D, H.264/HEVC/AV1/VP9, Atmos/DTS:X/TrueHD/DTS-HD MA, 7.1/5.1/stereo, audio languages and VOST subtitle language codes. Stereo is disabled by default. HDR labels also require HDR playback to be enabled on that Lowfin device.

These are Lowfin-native overlays attached to the card frame, not modified server images. Other clients remain unchanged. Series and season DVD labels require a strict majority of visible episodes; other resolution labels use the highest available resolution. Container codec and language labels describe available episodes, not necessarily every episode. The background index persists facts and reconciles them every 24 hours and on library events.

Language labels use compact ISO codes rather than flags. Small cards bound the tag area and show an overflow count. Older clients continue to receive the original DVD/5.1/HDR mask.

Detection categories were informed by [JellyTag](https://github.com/Atilil/jellyfin-plugins); see [third-party notices](THIRD_PARTY.md). No upstream image assets are used.

## Dashboard navigation and Discord presence (0.7.0)

Lowfin Companion appears in the dashboard sidebar. Its icon is the original two-path Lowfin **L**, with no text or background. Jellyfin's supported branding CSS registers a font containing only the private-use icon glyph; other custom CSS is retained. Disable **Use the Lowfin L in the dashboard sidebar** to use the standard extension icon instead. Reload the web dashboard after changing this option. Settings are grouped into overview, cover tags/index, ratings providers, Tracearr and Discord presence.

Discord presence is optional and **off by default**. It observes Jellyfin playback events without changing playback reporting, watched status or completion rules. Configure the Discord application ID, then add enabled mappings between Jellyfin users and their Discord tokens. Only explicitly mapped users participate. A single presence is maintained per mapped user; the latest reported session wins when the same account plays on multiple devices.

Supported media include movies, episodes, music, books, live TV and an optional fallback for other media. Live TV displays the channel above the current programme, with a programme start/end time bar when EPG timings are valid. Without programme information it repeats the channel title. Paused playback has no running time bar. The dashboard also controls names/templates, activity display, online state, update throttling, idle cleanup, link buttons and artwork. An idle timeout of zero disables idle cleanup; playback-stop still clears presence.

This compatibility mode uses a **Discord user-token gateway connection**, not official desktop RPC or a bot integration. Discord prohibits automated user accounts. Tokens are sensitive. Only their encrypted values are persisted in the plugin configuration and returned by Jellyfin's administrator configuration API. New tokens are submitted through that API for encryption before saving. This feature is not enabled or connected during automated tests. No real Discord credentials are included in the repository.

Configure Discord directly in the Companion dashboard. There is no standalone-settings import. Existing Companion user mappings remain stored on upgrades. Disable/remove a separate Discord presence plugin before enabling the Companion integration to avoid duplicate connections.

Local artwork uploading is separately **opt-in**. It sends the original pipeline’s square PNG (or original-image fallback) to the configured HTTPS upload service (default: uguu.se), then registers the external image with Discord. Jellyfin access tokens and private server image URLs are not sent. Leave uploading disabled to use only your configured public fallback/pause image URLs. Remove the mapping to delete a stored token, and revoke exposed tokens through Discord.

The sidebar SVG/font are embedded in the plugin DLL. Rebuilding the optional font from its source paths requires Python `fonttools` and `brotli`: `python3 tools/build-mark.py`. Ordinary plugin builds use the committed font and need neither package.

Optional dashboard behavior test (Node.js 20+ and `jsdom` 26 available on NODE_PATH): `node tools/test_dashboard.cjs`. It verifies loading/saving, preserved future settings, tag switches, mapping validation and interval limits. Test the newer server API packages with `dotnet test tests/Lowfin.Companion.Tests/Lowfin.Companion.Tests.csproj -c Release -p:TestJellyfinVersion=12.3.0`.

The gateway follows the connection behavior of the supplied original source: discover `/api/gateway`, retain its unversioned URL (fallback `wss://gateway.discord.gg/`), send the expected client headers, accept fragmented user READY messages up to 32 MiB, and replay queued presence after READY. Close codes and bounded-message failures are logged without tokens or raw gateway payloads. Public paused images are resolved to Discord external assets; absent optional fields are omitted. Pause/resume changes bypass ordinary seek throttling.

### Discord token storage and backups

Discord tokens use authenticated .NET Data Protection with a stable plugin application/purpose and per-Jellyfin-user binding. Existing cleartext Discord tokens are migrated when the updated plugin starts; new/replaced tokens are encrypted before configuration persistence. Unchanged encrypted values remain unchanged. The dashboard never refills a token field with the raw token: leave it empty to retain the stored value, enter a replacement to change it, or remove the mapping to delete it. Changing the mapped Jellyfin user requires entering the token again.

The persistent key ring is under `<Jellyfin data directory>/lowfin-companion/secret-keys/`. On Unix the directory is owner-only (`0700`) and generated key files are restricted to `0600`. The key ring is separate from the plugin configuration and must be included in confidential server backups. Losing it requires restoring the keys or re-entering the tokens. Decryption failures retain the encrypted configuration and skip the affected presence instead of sending ciphertext as a token.

This protects a copied configuration file, not a compromised server: on Linux the local Data Protection key material is not itself wrapped with a separate certificate/master password. Anyone who obtains both the configuration and key ring can decrypt the tokens. The token must exist in server memory while connecting. Older backups containing cleartext tokens are not rewritten. Keep the same persistent data volume for container recreation. Do not put keys, configuration or real tokens into Git. No real server keys are bundled in release ZIPs.

Implementation reference: [Microsoft: Data Protection without DI, persistent keys and at-rest limits](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/configuration/non-di-scenarios?view=aspnetcore-10.0).

### Discord artwork pipeline

The Discord artwork workflow combines cover rendering, a persistent upload cache and external-asset registration. Episodes try their season poster, then the series poster, then the episode image. Sources may be local files or image URLs. Posters are centered on a 512 × 512 PNG over a blurred background; composition failure falls back to the raw image. Upload MIME types match PNG/JPEG bytes. Successful uploads are persisted for 150 minutes and shared across episodes with the same source poster; rejected uploads trigger a 15-minute cooldown. Discord receives a registered `mp:external/...` asset, not a Jellyfin-private image URL.

Local cover uploads still require **Upload local cover artwork** to be enabled. The Companion periodically refreshes artwork and retries missing assets at most every two minutes on continuing playback reports. Reusing an unchanged image does not resend the activity. New logs distinguish the selected image source, upload errors and whether Discord accepted an asset. See the source provenance and third-party notices for the adapted components.
