# ADR 0005: Virtual shards and placement epochs

- Status: accepted
- Date: 2026-07-20

## Context

Hashing a complete collection to one Raft data group caps that collection at one group's capacity. Changing a modulo divisor also remaps almost every key and cannot safely describe an online move.

## Decision

Persist a fixed set of virtual shards in each collection definition. A stable hash of `(collectionId, documentId)` selects a virtual shard, and the catalog maps that shard to a bounded physical data-group pool. Every placement carries a monotonically increasing routing epoch. Data commands contain the virtual shard and epoch; stale epochs are rejected.

Writes are atomic within one physical data group. An atomic request spanning groups is rejected with `cross_shard_atomic_unsupported`; a non-atomic request is partitioned and returns its normal per-document outcome. Reads apply consistency barriers to every physical group represented by the collection. Vector searches then run against each group's routed ID set and merge shard-local top-K candidates into a deterministic global top-K. Text and hybrid modes retain the globally merged BM25 read model so shard-local BM25 scores are never treated as directly comparable.

Online movement is cataloged as `Active → Copying → CatchingUp → Switching → Draining → Active`. The source serves writes until switching, the target serves them after switching, and every transition increments the epoch. Copying persists a checksummed snapshot checkpoint containing only the selected virtual shard. Catch-up persists ordered upsert/delete operations, verifies snapshot-plus-delta against the current source checksum, and records the replay high-water mark. Switching revalidates the live checksum and returns to catch-up if the source changed. Group Raft snapshots likewise contain only documents routed to that group.

## Consequences

The number of virtual shards can remain stable while their physical placement changes. Routers can detect stale decisions, snapshots do not duplicate unrelated shards, and a controller can resume an interrupted move from catalog state. Cross-group transactions remain deliberately unsupported. [ADR 0006](0006-shared-nothing-data-group-placement.md) applies this protocol to a shared-nothing topology where nodes host only their assigned groups.
