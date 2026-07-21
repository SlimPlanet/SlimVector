# Architecture

> [Documentation index](README.md) · [User guide](user-guide.md) · [ADRs](README.md#design-and-development)

SlimVector separates domain rules, durable state, derived search structures, consensus, geographic replication, application orchestration, HTTP transport, and the typed client. The same storage state machine is used in single-node and clustered modes.

## Request paths

A write follows this path:

1. The Minimal API binds either source-generated JSON or generated, untrusted-data MessagePack contracts and converts metadata to typed values. JSON remains the default public representation.
2. Domain, batch-size, text-size, and admission checks run before durable mutation.
3. The adaptive scheduler admits the request into bounded global, client, collection, and shard queues.
4. A per-shard worker builds a fair batch by rotating between collections, then proposes one deterministic MemoryPack command.
5. Single-node mode applies it to the local RF1 group. Cluster mode resolves every document through its immutable virtual shard and commits the partitioned command through the corresponding data Raft leader. An API node that does not host that group forwards the exact idempotent MemoryPack command over HTTP/2. The payload is HMAC-signed when the administrator key is configured, as required by the production hardening guidance.
6. The storage state machine writes an immutable checksummed MemoryPack segment and atomically replaces its small JSON manifest.
7. The open collection runtime updates in-memory state and writes a versioned derived-index snapshot. A stale snapshot is safe because its document signature forces reconstruction.
8. When geographic replication is enabled, a separate durable outbox forwards the semantic command to the secondary.

A query fans out only to represented data groups. Linearizable requests use group leaders; stale requests may use healthy followers. Each group evaluates metadata and returns an oversampled local top-K, and the coordinator performs a stable score/id merge. Text and hybrid searches first collect BM25 corpus/term statistics from every group, aggregate them globally, then score locally with the same global statistics. Hybrid search combines global vector/text ranks with weighted reciprocal-rank fusion.

## Consensus topology

SlimVector is shared-nothing. A three-voter catalog records nodes, roles, capacities, zones, data groups, replica sets, collection shard maps, placement epochs and durable movement state; other nodes keep its persistent local cache. Each data group normally has its own RF3 replica set and runs only on those nodes. Groups have independent DotNext endpoints, WALs, leaders, commit indexes and snapshots, using ports allocated persistently from each node's configured range.

Collections have 1,024 virtual shards by default. The shard count is persisted and immutable; only the epoch-versioned shard-to-group map changes. This decouples a collection from one server and allows fine-grained redistribution across dozens of nodes. Replica relocation temporarily adds a fourth voter while Raft installs/catches up its state. Shard relocation copies a checksummed snapshot under a barrier, replays ordered deltas through the target group, switches authority by catalog epoch, and deletes the source only afterward.

`leader` reads execute a leader barrier, `linearizable` reads use a quorum-backed read barrier, and `stale` reads deliberately skip it. Readiness is based on the catalog cache and locally assigned groups, not every group in the cluster.

## Durable state

The catalog, immutable segments, and manifests are authoritative. A node stores authoritative collection files only below `data-groups/{groupId}/collections/{collectionId}` for groups it hosts. Tombstones preserve deletes until compaction. Human-inspectable catalog/manifests use versioned JSON plus atomic replacement; high-volume segment bodies use MemoryPack and segment/derived-index files carry SHA-256 checksums. Startup reads legacy JSON segments as well as current `SVS2` segments, replays them by sequence, and reconciles orphaned files left by a crash.

Flat vectors, HNSW graphs, BM25 term structures, and metadata structures are also stored in a combined `search-index-v1` derived snapshot. The snapshot includes the ordered id/version signature and index configuration. Any mismatch, malformed payload, or checksum failure prevents reuse; authoritative segments can reconstruct all four indexes.

Raft application snapshots contain state-machine state and idempotency history. A late follower can install the snapshot without forcing every application collection to remain open.

## Geographic boundary

Local Raft and inter-zone replication are intentionally separate. Raft protects a local failure domain with synchronous majority consensus. Geographic replication transports already accepted semantic operations through a durable ordered outbox, HMAC-authenticated HTTP, receiver sequence checkpoints, duplicate receipts, and divergence checks. The DR secondary is read-only until explicitly promoted.

## Backups

A backup establishes linearizable barriers, serializes each collection to a versioned MemoryPack payload, hashes plaintext with SHA-256, optionally encrypts it with AES-256-GCM, and uploads it under a content-addressed key. The manifest references collection blobs and the previous backup. Retention deletes obsolete manifests and then unreferenced blobs.

Backups are collection-consistent at their individual barriers; they are not a cross-collection transaction. Restore verification reads and authenticates every referenced blob before a full restore starts deleting current collections.

See [ADR 0002](adr/0002-multi-raft-groups.md), [ADR 0003](adr/0003-immutable-storage-and-derived-indexes.md), [ADR 0004](adr/0004-separate-geographic-replication.md), [ADR 0005](adr/0005-virtual-shards-placement-epochs.md), and [ADR 0006](adr/0006-shared-nothing-data-group-placement.md).
