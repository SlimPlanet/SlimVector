# ADR 0002: Independent catalog and data Raft groups

- Status: accepted
- Date: 2026-07-18

## Context

A single consensus log would serialize every collection write and make one hot collection the throughput limit for the database. Collection definitions and placement also have different access patterns from document mutations.

## Decision

Run one catalog group and a configurable number of independent data groups on every cluster node using DotNext.Net.Cluster 6.x. Give every group an independent TCP port offset, WAL, election, snapshot schedule, applied index, and idempotency history. Batch/fairness workers operate per data group. ADR 0005 supersedes the original whole-collection hash with persisted virtual-shard placement while retaining this bounded physical group pool.

The catalog is the authority for definitions/placement. Data commands include the collection definition needed for deterministic state-machine validation. Changing the number of data groups is an explicit resharding event, not a transparent configuration edit.

## Consequences

Unrelated shards can commit concurrently and failures/metrics are isolated by group. Operators must expose consecutive ports and keep topology identical. Cross-shard transactions are not provided.
