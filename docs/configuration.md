# Configuration

SlimVector binds every section to a dedicated options type and validates it at startup. ASP.NET Core environment-variable syntax applies: replace `:` with `__`, for example `Storage__Path=/data`. Array indexes use `__0`, `__1`, and so on.

## Sections

| Section | Important settings | Safe default |
| --- | --- | --- |
| `Storage` | `Path`, `FlushToDisk`, `MaximumSegmentsBeforeCompaction` | local `data`, fsync, 64 segments |
| `Collections` | `IdleTimeout`, `EvictionSweepInterval`, `MaximumOpenCollections` | 15 min, 1 min, 128 |
| `VectorIndex` | `AutoHnswThreshold`, `HybridCandidateMultiplier`, `MaximumSearchLimit` | 50,000, 4, 1,000 |
| `TextIndex` | `Bm25K1`, `Bm25B`, `MaximumTermsPerDocument` | 1.2, 0.75, 100,000 |
| `MetadataIndex` | `IndexByDefault`, `MaximumFilterDepth` | true, 32 |
| `Raft` | mode, node/endpoints, members, groups, elections, snapshots | single-node |
| `GeoReplication` | sender/receiver flags, mode, endpoint, secret, retry/outbox | disabled |
| `AdaptiveBatching` | size/window/byte bounds | 1–256, 0–10 ms, 4 MiB |
| `Backpressure` | global/client/collection/shard capacities and concurrency | bounded defaults |
| `Backup` | schedule, provider, retention, encryption, S3 | disabled filesystem |
| `Api` | prefix, batch/body limits, timeout, administrator routes/key | `/api/v1`, admin off |
| `Observability` | metrics, structured logging, service name, slow-query threshold | enabled metrics, 500 ms |

The complete single-node example is [appsettings.json](../src/SlimVector.Api/appsettings.json); [compose.cluster.yml](../compose.cluster.yml) is the complete three-node override.

`MetadataIndex:IndexByDefault=false` marks newly created collections as unindexed for metadata. Filters remain correct through an explicit document scan, but broad filters cost O(document count) and the metadata derived snapshot remains empty. Existing collection definitions retain their stored setting.

## Critical validation

Startup is refused when, among other cases:

- a cluster has fewer than three unique IP endpoints, omits the local endpoint, or lacks one valid public API mapping per member;
- consecutive group ports would exceed the TCP range, or election time is not more than twice the heartbeat interval;
- geographic replication lacks an absolute secondary URI or a 32-character shared secret;
- S3 lacks endpoint, bucket, access key, or secret key;
- backup encryption lacks a base64-encoded 256-bit key;
- administrator routes are enabled with a key shorter than 32 characters;
- any queue, timeout, batch, request, index, or retention bound is inconsistent.

## Production secrets

Do not commit `GeoReplication:SharedSecret`, `Backup:EncryptionKey`, S3 credentials, or `Api:AdminApiKey`. Inject them through your orchestrator's secret store. Use independent geo and admin secrets. Rotate a geo secret by coordinating sender and receiver because events signed with the old secret will be rejected.

Generate a backup encryption key with:

```bash
openssl rand -base64 32
```

Changing that key makes existing encrypted backups unreadable unless the old key is retained for restore.

## Port allocation

`Raft:PublicEndpoint` is the catalog group's base TCP address. Data group `n` listens on base port `n + 1`. With base `3262` and two data groups, expose 3262, 3263, and 3264. `Raft:Members` contains base endpoints; SlimVector applies the same deterministic offset on every node.

`Raft:MemberApiEndpoints` are client-visible redirect addresses, not Raft transport addresses. In Kubernetes these should be stable ingress/service URLs; in the supplied Compose file they are host ports 8081–8083.
