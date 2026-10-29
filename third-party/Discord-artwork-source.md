# Discord artwork source provenance

- Source: components supplied privately for this project.
- Input: original source archive supplied by the user (`jellyfin-discord-rpc-main.zip`).
- Archive SHA-256: `6c23de28ff4b1aa5d428a4fcf218f87987bb3e2db0e057952c840071cf08bee7`.
- Adapted components: `Services/CoverService.cs`, `Services/ArtworkUploadCache.cs`, `Discord/DiscordAssetService.cs` and the image selection/upload orchestration from `PresenceManager`.
- The original process is retained: season → series → episode, local/HTTP source read, 512-pixel square PNG with blurred background, raw-image fallback, correct PNG/JPEG content type, Uguu-compatible multipart upload, 150-minute persistent upload reuse, 15-minute upload cooldown and Discord external-assets registration.
- Integration changes: Companion namespaces/shared client header, Companion-specific cache filename, existing configurable upload URL/switch, redacted URL/body logging, and bounded retries/refresh in the Companion playback listener. Server tokens remain encrypted by the separate Companion token store.
- The provided source archive contains no LICENSE file; this provenance notice does not grant rights on behalf of the upstream author. The Companion's GPL notice describes its own original work; adapted upstream components retain their authorship and are identified separately.
