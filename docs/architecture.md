# Architecture

SlimVector separates domain rules, durable state, derived search structures, consensus, geographic replication, application orchestration, HTTP transport, and the typed client. The same storage state machine is used in single-node and clustered modes.

## Request paths

A write follows this path:

1. The Minimal API binds a source-generated JSON contract and converts JSON metadata to typed values.
2. Domain, batch-size, text-size, and admission checks run before durable mutation.
3. The adaptive scheduler admits the request into bounded global, client, collection, and shard queues.
4. A per-shard worker builds a fair batch by rotating between collections, then proposes one deterministic MemoryPack command.
5. Single-node mode applies it directly. Cluster mode commits it through the collection's data Raft group.
6. The storage state machine writes an immutable checksummed segment and atomically replaces its manifest.
7. The open collection runtime updates in-memory state and writes a versioned derived-index snapshot. A stale snapshot is safe because its document signature forces reconstruction.
8. When geographic replication is enabled, a separate durable outbox forwards the semantic command to the secondary.

A query first applies the selected read barrier, opens the collection on demand, evaluates the metadata candidate set, then runs Flat/HNSW and/or BM25. Hybrid search combines ranks with weighted reciprocal-rank fusion.

## Consensus topology

Every cluster node hosts a catalog group plus `Raft:DataGroupCount` independent data groups. The catalog replicates collection definitions and deterministic placement. A collection id hashes to one data group, so different shards can commit concurrently. Each group has its own DotNext TCP endpoint, WAL, election, commit index, and snapshots. The API returns HTTP 307 with the mapped public API address when a follower receives a leader-only write.

`leader` reads execute a leader barrier, `linearizable` reads use a quorum-backed read barrier, and `stale` reads deliberately skip it. Readiness is false until all local groups are usable.

## Durable state

The catalog, immutable segments, and manifests are authoritative. Tombstones preserve deletes until compaction. Catalog/manifests use atomic replacement; segment and derived-index files carry SHA-256 checksums. Startup replays valid segments by sequence and reconciles orphaned files left by a crash.

Flat vectors, HNSW graphs, BM25 term structures, and metadata structures are also stored in a combined `search-index-v1` derived snapshot. The snapshot includes the ordered id/version signature and index configuration. Any mismatch, malformed payload, or checksum failure prevents reuse; authoritative segments can reconstruct all four indexes.

Raft application snapshots contain state-machine state and idempotency history. A late follower can install the snapshot without forcing every application collection to remain open.

## Geographic boundary

Local Raft and inter-zone replication are intentionally separate. Raft protects a local failure domain with synchronous majority consensus. Geographic replication transports already accepted semantic operations through a durable ordered outbox, HMAC-authenticated HTTP, receiver sequence checkpoints, duplicate receipts, and divergence checks. The DR secondary is read-only until explicitly promoted.

## Backups

A backup establishes linearizable barriers, serializes each collection to a versioned MemoryPack payload, hashes plaintext with SHA-256, optionally encrypts it with AES-256-GCM, and uploads it under a content-addressed key. The manifest references collection blobs and the previous backup. Retention deletes obsolete manifests and then unreferenced blobs.

Backups are collection-consistent at their individual barriers; they are not a cross-collection transaction. Restore verification reads and authenticates every referenced blob before a full restore starts deleting current collections.

See [ADR 0002](adr/0002-multi-raft-groups.md), [ADR 0003](adr/0003-immutable-storage-and-derived-indexes.md), and [ADR 0004](adr/0004-separate-geographic-replication.md).
