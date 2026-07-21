# ADR 0002: Independent catalog and data Raft groups

- Status: superseded in part by [ADR 0006](0006-shared-nothing-data-group-placement.md)
- Date: 2026-07-18

## Context

A single consensus log would serialize every collection write and make one hot collection the throughput limit for the database. Collection definitions and placement also have different access patterns from document mutations.

## Decision

The original decision ran one catalog group and a configurable number of independent data groups on every cluster node using DotNext.Net.Cluster 6.x. Every group has an independent port, WAL, election, snapshot schedule, applied index, and idempotency history. Batch/fairness workers operate per data group. ADR 0005 superseded whole-collection hashing with persisted virtual-shard placement; ADR 0006 then replaced identical all-node membership with dynamic per-group replica sets.

The catalog is the authority for definitions/placement. Data commands include the collection definition needed for deterministic state-machine validation. Changing the number of data groups is an explicit resharding event, not a transparent configuration edit.

## Consequences

Unrelated shards can commit concurrently and failures/metrics are isolated by group. Operators reserve a persistent port range, but the catalog now decides which subset each node starts. Cross-group transactions are not provided.
