# Geographic replication and disaster recovery

> [Documentation index](README.md) · [Cluster](cluster.md) · [Security](security.md)

Geographic replication is a second layer above local consensus. It does not add remote members to a Raft group.

## Simulated two-zone deployment

```bash
docker compose -f compose.geo.yml up --build
```

The primary is `http://localhost:8090`; the read-only secondary is `http://localhost:8091`. The example secret is intentionally non-production and must be replaced.

The primary commits locally, writes a versioned event into its filesystem outbox, and sends it in strict sequence to `/internal/geo/replicate`. Requests carry `X-SlimVector-Signature`, an HMAC-SHA256 of the exact MemoryPack body. The receiver persists the highest sequence and event receipt, applies through its local consensus coordinator, and returns an acknowledgement.

Duplicate delivery with the same id/content is acknowledged without reapplying. A sequence gap is rejected so the sender retries the missing event first. Reuse of an event id with different content is a divergence and is rejected/metriced.

The essential primary settings are `Enabled=true`, a secondary URL, a unique origin id, and a shared secret of at least 32 characters. The secondary sets `AcceptIncoming=true` and remains read-only:

```json
{
  "GeoReplication": {
    "Enabled": false,
    "AcceptIncoming": true,
    "ReadOnlySecondary": true,
    "OriginId": "secondary-eu-west",
    "SharedSecret": "<secret-distinct-from-admin-key>",
    "StatePath": "/var/lib/slimvector/geo-replication"
  }
}
```

The two sites must use compatible collection definitions and vector dimensions. HMAC does not encrypt document content; use TLS or mTLS across the WAN.

## Modes and expected RPO/RTO

| Mode | Acknowledgement | Expected RPO | Availability trade-off |
| --- | --- | --- | --- |
| `Asynchronous` | after local commit and durable outbox admission | up to the reported pending outbox/lag if the primary site is destroyed | remote outage does not stop local writes until the bounded outbox is saturated |
| `Synchronous` | after secondary acknowledgement | zero for operations acknowledged to the caller | remote latency/outage is on the write path; the local operation may already be committed when an acknowledgement fails |

RPO is an operational target, not a wall-clock guarantee: monitor `slimvector_geo_pending_events` and `slimvector_geo_replication_lag_seconds`. In async mode an event survives process restart in the primary outbox. If both the primary volume and the remote copy are lost before acknowledgement, that event is lost.

Expected RTO is the time to verify lag/divergence, fence the old primary, change the secondary configuration to writable, restart it, and redirect clients. With rehearsed automation this is normally minutes; SlimVector deliberately does not auto-promote because split-brain data loss is worse than a controlled outage.

## Promotion runbook

1. Fence the old primary at the network and compute layers.
2. Confirm receiver divergence is zero and record pending/lag metrics. If the old primary is reachable, allow its outbox to drain.
3. Stop the secondary.
4. Set `GeoReplication__AcceptIncoming=false`, `GeoReplication__ReadOnlySecondary=false`, and choose whether this site now replicates to a new secondary.
5. Start it and wait for `/health/ready`.
6. Run collection counts and representative queries, then switch client traffic.
7. Rebuild the former primary from a verified backup or from the promoted site; never simply enable both as writable peers.

## Network recovery

The sender retries with bounded exponential backoff between `RetryMinimumDelay` and `RetryMaximumDelay`. Sequence and receipt checkpoints are on disk, so restart resumes from the first unacknowledged event. `QueueCapacity` is explicit backpressure: once exhausted, new writes fail rather than accepting an unreplicable, unbounded backlog.

Use TLS between zones or terminate TLS at a mutually authenticated private gateway. HMAC authenticates content but does not encrypt it.
