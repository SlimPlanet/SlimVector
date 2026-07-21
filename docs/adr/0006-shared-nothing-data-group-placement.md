# ADR 0006: Shared-nothing data-group placement

- Status: accepted
- Date: 2026-07-20

## Context

Running every data group on every server replicates the complete database to every disk. Adding nodes can improve compute and replica count but cannot increase useful storage capacity linearly. Safe scale-out also requires placement to survive coordinator and catalog-leader changes.

## Decision

Keep a three-voter Raft catalog containing nodes, zones, capacities, dynamic data groups, replica sets, placements, epochs, and durable movement records. Every RF3 data group runs only on its assigned nodes; non-catalog nodes maintain a signed persistent catalog cache. Servers reserve a stable data-port range so groups can start, transfer leadership, and stop dynamically.

Store authoritative files below `data-groups/{groupId}/collections/{collectionId}` only on group replicas. Route any API request through document → virtual shard → data group → eligible replica/leader, forwarding signed MemoryPack over HTTP/2 when the receiving API node is not a holder. Fan out reads to represented groups and merge deterministically; aggregate BM25 statistics globally before text or hybrid scoring.

Relocate a replica by temporarily adding a fourth member, installing/catching up its Raft snapshot, transferring leadership if required, and removing the source. Relocate a virtual shard with the ADR 0005 snapshot/delta/checksum/epoch protocol. Persist approved checkpoints so a new coordinator resumes idempotently.

Balance by occupied bytes, free capacity, load, and zone anti-affinity. Default to RF3, 15% reserve, 65% target, 80% high watermark, 32-GiB group targets, and explicit administrator approval for capacity rebalancing. Repair under-replication automatically after the configured failure delay when quorum and RF headroom permit it.

## Consequences

With homogeneous disks, useful advertised capacity approaches `(sum of disk × 0.85) / RF`; adding three nodes to an RF3 three-node cluster approximately doubles capacity after redistribution. No node needs the complete collection, and each document has exactly RF authoritative copies.

Operations become more involved: node identity, port ranges, zones, topology health, movement throughput, and disk watermarks must be monitored. Cross-group atomic transactions remain unsupported. Distributed storage v2 is not rolling-compatible with the former layout, so migration uses verified backup/restore into a new cluster with the old cluster preserved for rollback.
