# Incremental indexes and cache updates

The cover-tag index follows Jellyfin library events and performs a daily reconciliation for missed changes. A repeated check still visits library records, but reuses saved media facts when Jellyfin's revision is unchanged. It does not reopen every media source or recompute every series and season.

Changed media update their affected parents. Series and season calculations use native user-scoped episode queries so hidden libraries and parental restrictions remain respected. Unchanged per-user summaries are reused when both their input facts and native visibility are unchanged. Access changes trigger recalculation. Custom ancestor implementations retain the existing per-container query path.

An older index without revision/visibility information needs one revalidation. Initial indexing, changed access rules and recovery after an event-queue overflow can still require a full calculation. Events arriving during a scan remain queued unless that exact queued revision was consumed.

The administrative `badges/status` endpoint and completion logs report reused media, reused summaries and recalculated summaries. These distinguish actual rebuilding from checking an existing index. Timings depend on the Jellyfin database, storage and library size; a small fixture does not predict a production scan time.

The metadata feed keeps its durable history. Delivery pages coalesce repeated notifications for the same item, advancing the cursor only over consumed records. New revisions, image updates, computed tag changes and removals still reach Lowfin. Missing history and changed access still produce explicit resets rather than silently skipping changes. This does not require a new client protocol.

Audio measurements have a separate optional index and task. See [audio loudness](audio-loudness.md) for enablement, persistence and initial-scan costs. None of these indexes changes media files or global Jellyfin completion settings.
