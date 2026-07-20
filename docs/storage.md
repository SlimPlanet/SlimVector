# Storage

The filesystem engine is authoritative in both execution modes. Each collection owns a directory containing immutable operation segments, an atomic manifest, recoverable obsolete segments, and checksummed derived indexes.

## Layout

```text
data/
  storage-format-v2.json
  cluster-topology-v2.json
  data-groups/<group-id>/collections/<collection-id>/
    manifest.json
    segments/<sequence>-<id>.segment
    derived/search-index-v1.bin
    obsolete/<manifest-generation>/...
  deleted/...
  raft/<node-id>/<group-id>/...
  geo-replication/...
```

The topology catalog and collection manifests are small, infrequently updated, operator-inspectable control files. They remain versioned JSON written through a temporary file, flush, and atomic rename; their durability cost is dominated by the atomic filesystem protocol rather than serialization. The storage-format marker and resumable rebalance journal follow the same rule.

Each node creates only the `data-groups` directories whose replica sets include that node; it never materializes the complete collection unless it owns every group (the normal single-node RF1 case). Authoritative segment bodies use a versioned MemoryPack payload prefixed by the `SVS2` magic and protected by the existing SHA-256 header. This avoids JSON number parsing and reduces vector-heavy bytes on the write/replay path. Legacy v1 JSON segments remain readable; the next real compaction rewrites their live state as v2 MemoryPack. Deletes are stored as tombstone operations, so replay cannot resurrect an older value.

## Commit and recovery

A segment is written and flushed before its manifest is replaced. Startup enumerates segments, verifies their checksums and collection ids, replays them in monotonically increasing sequence, and reconciles the manifest with valid files. This handles a process failure after segment rename but before manifest replacement.

Corruption fails closed with `storage_corrupted`; SlimVector does not silently skip a bad authoritative segment. Restore from a verified backup or replace the failed replica from Raft before serving traffic.

## Compaction

When active segments reach `Storage:MaximumSegmentsBeforeCompaction`, the writer builds one new segment containing current live documents, moves superseded segments to a generation-specific `obsolete` directory, then atomically replaces the manifest. `CompactAsync` is also available to operators embedding the Application service.

Obsolete and deleted collection directories remain recoverable filesystem material. Their lifecycle is separate from backup retention; monitor disk growth and remove them only under an explicit operational retention policy.

## Derived indexes

`search-index-v1.bin` contains a MemoryPack snapshot of Flat or HNSW vector state, BM25 state, and metadata state. The storage wrapper adds a SHA-256 header. The inner snapshot records format version, effective vector kind, index settings, and a SHA-256 signature derived from sorted document ids and versions.

Derived data is never authoritative. A checksum failure is reported as storage corruption; a valid but stale/incompatible inner snapshot is ignored and rebuilt from segments. After the next mutation the current snapshot replaces it atomically.

## Filesystem assumptions

Use a local durable filesystem that provides atomic rename within one volume. Do not put the live data directory on eventually consistent object storage. In cluster mode each node needs a distinct volume; never share one storage path between Raft nodes.

## Storage v1 to v2

The distributed layout is intentionally not rolling-compatible with v1. Keep the old cluster read-only, create and verify a v1 backup, restore it into a fresh v2 cluster, then compare collection counts and logical checksums before moving client traffic. Restore recomputes the immutable 1,024-shard placement and rebuilds all derived indexes. Rollback points clients back to the preserved v1 cluster; do not reuse its volumes in v2.
