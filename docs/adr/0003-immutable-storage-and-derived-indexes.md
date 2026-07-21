# ADR 0003: Immutable authoritative segments and disposable index snapshots

- Status: accepted
- Date: 2026-07-18

## Context

Search indexes optimize different workloads and their formats evolve. Treating a graph or posting file as the only copy of a document makes crash recovery and upgrades fragile.

## Decision

Keep collection operations in immutable, checksummed, sequence-ordered segments behind an atomic manifest. Represent deletion as a tombstone and periodically compact current state. Persist the configured vector index, BM25, and metadata structures together as a versioned, checksummed derived snapshot carrying the document id/version signature and index settings.

On an inner signature/configuration mismatch, rebuild from authoritative segments. On an outer checksum failure, surface corruption instead of silently hiding unexpected disk damage.

## Consequences

Recovery is deterministic and index formats can be replaced. Cold load after a missing/stale snapshot costs reconstruction time, and derived snapshots consume additional disk. Compaction and lazy eviction bound replay and memory costs.
