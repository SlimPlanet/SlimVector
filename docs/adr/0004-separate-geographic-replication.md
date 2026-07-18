# ADR 0004: Keep geographic replication above local Raft

- Status: accepted
- Date: 2026-07-18

## Context

Stretching a local Raft quorum across high-latency regions couples availability to WAN health and blurs disaster-recovery policy with local consensus.

## Decision

Use local Raft only inside one failure domain. After local acceptance, place a versioned semantic event in a durable bounded outbox and deliver it in strict sequence to an HMAC-authenticated receiver in another deployment. Persist sender and receiver checkpoints, deduplicate exact replay, reject gaps, and flag divergent event-id reuse. Keep the secondary read-only until deliberate promotion.

Support asynchronous mode by default and synchronous remote acknowledgement when its latency/availability trade-off is explicitly chosen.

## Consequences

Local cluster elections do not depend on WAN health and RPO is observable as outbox lag. Asynchronous mode can lose the unacknowledged tail if the entire primary site is destroyed. Promotion is an operator runbook because automatic multi-primary failover would risk split brain.
