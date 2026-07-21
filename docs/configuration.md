# Configuration

> [Documentation index](README.md) · [User guide](user-guide.md) · [Security](security.md)

Every section binds to a dedicated C# options type and is validated with `ValidateOnStart`. Standard ASP.NET Core precedence applies: environment variables override appsettings values, `:` becomes `__`, and arrays use numeric suffixes, for example `AutoIndex__AllowedIndexes__0=Hnsw`. Durations use .NET `TimeSpan` syntax such as `00:00:30`; sizes are bytes unless their name states otherwise.

Complete examples are [single-node appsettings](../src/SlimVector.Api/appsettings.json), [cluster appsettings](../config/appsettings.Cluster.example.json), and [evolving Compose cluster](../compose.cluster.yml).

| Section | Controls |
| --- | --- |
| `Storage`, `Collections` | durable root/fsync/compaction; lazy-open idle timeout, sweep, open limit |
| `VectorIndex` | hybrid candidate multiplier, search limit, legacy Auto threshold |
| `AutoIndex` | allowed kinds, assessment/migration intervals, hysteresis, recall/gain floors, validation sample, size/memory/churn thresholds |
| `Hnsw` | `M`, `EfConstruction`, `EfSearch` defaults for new collections |
| `Ivf` | list/probe counts and deterministic training iterations |
| `PQ` | subvectors, centroids, iterations, exact-rerank multiplier |
| `DiskAnn` | artifact path, degree/search/beam, delta merge, page/cache sizes, retained generations |
| `TextIndex`, `MetadataIndex` | BM25 limits/scoring and filter indexing/depth |
| `Raft`, `ClusterMembership` | bootstrap/join mode, node identity/endpoints/zone/capacity, persistent data-port range, election/snapshot/transport and safe membership changes |
| `Rebalancing` | controller enablement, manual approval, move concurrency, reconciliation/cooldown intervals and minimum improvement |
| `DataPlacement` | RF, disk reserve, target/high watermark, 32-GiB group target, minimum group density and transfer budget |
| `AdaptiveBatching`, `Backpressure` | bounded command/byte/window targets, global/client/collection/group queues and concurrency |
| `RateLimit` | global/client/collection/read/write/admin token buckets, reserved read/write fractions, adaptive recovery |
| `Backup`, `GeoReplication` | provider/schedule/retention/encryption/S3; separate signed DR outbox/receiver |
| `Api`, `Observability` | prefix/body/batch/timeout/admin auth; metrics/logging/slow-query threshold |

The HNSW/IVF/PQ/DiskANN sections populate a new collection's configuration when `vectorIndex` is omitted. An explicitly supplied collection configuration is stored in the catalog and remains authoritative across restart.

`Raft:DataGroupCount` seeds the bounded data-group pool at bootstrap and remains for configuration compatibility. It is not a declaration that every server stores every group: the catalog's dynamic `DataGroupDescriptor` replica sets become authoritative, and a joined node starts only groups assigned to it. `DataPortRangeStart`/`DataPortRangeCount` reserve stable local ports for those dynamic assignments.

## Bootstrap versus join mode

Initial cluster voters set `Raft:JoinExistingCluster=false` and provide three catalog `Members` plus matching `MemberApiEndpoints`, `MemberNodeIds`, `MemberInternalEndpoints`, `MemberZones` and `MemberCapacityBytes`. These bootstrap arrays must be identical and ordered identically on the three voters. Every server also supplies its own stable `NodeId`, public/internal API endpoints, zone, usable disk capacity and persistent data-port range. A new server registers through `/admin/cluster/nodes/join`; the catalog records it, seeds its signed persistent catalog cache, computes a read-only capacity-aware plan, and waits for explicit approval before installing data-group replicas. Joined data nodes do not become catalog voters.

Critical startup failures include invalid/duplicate topology, missing local or API mappings, exhausted port offsets, bad election/heartbeat ratios, empty Auto allowed sets, non-divisible fixed IVF-PQ dimensions, invalid DiskANN page/cache/retention bounds, inconsistent queue/token reserves, unsafe timeouts, weak administrator/geo secrets, invalid backup encryption keys, and incomplete S3 credentials.

Join-mode nodes set `JoinExistingCluster=true` and leave every bootstrap member array empty. Their persistent `NodeId`, capacity, zone, internal/public endpoints, Raft host, and port range must match the subsequent node-registration request.

## Placement controller

`Rebalancing:ManualApproval` defaults to `true`: planning is read-only and a catalog leader starts no move until an administrator approves the returned plan. `MaximumConcurrentMoves` bounds simultaneous source/target pairs. Replica relocation first installs and catches up a temporary fourth member, transfers leadership if needed, then removes the old member. The staged catalog record survives coordinator changes and restart. Shard moves separately persist `Copying`, `CatchingUp`, `Switching` and `Draining` checkpoints. Setting `Enabled=false` disables background progression while keeping explicit administrator actions available.

`DataPlacement:ReplicationFactor=3` stores each data group on three distinct nodes and prefers distinct zones. Capacity is balanced by actual bytes and free space, not shard count. With homogeneous nodes, advertised usable capacity is `(sum(capacity) × (1 - ReserveRatio)) / ReplicationFactor`; the 65% target leaves maneuvering room before the 80% high watermark. `DataPlacement:MaximumTransferBytesPerSecond` is the operator transfer budget; Raft snapshot transport remains responsible for flow control. `FailureReplacementDelay` controls how long a node may stop reporting before the catalog leader marks it unavailable and automatically approves RF-repair moves; it does not bypass quorum requirements.

## Rate and congestion policy

Normal limits are token buckets at four scopes: global, client/API key, collection, and operation. The global pool is divided into shared, read-reserved, and write-reserved capacity; administrative requests use their separately bounded bucket and are not blocked by data-plane congestion. Adaptive refill drops immediately under queue, memory, Raft lag/quorum, or error pressure and recovers gradually over `RecoveryWindow`.

A contractual limit and congestion refusal are distinct 429 responses. Both include `Retry-After`; `X-SlimVector-RateLimit-Kind` is `contractual` or `congestion`, and the scope/reason is returned separately.

## Secrets

Do not commit `Api:AdminApiKey`, `GeoReplication:SharedSecret`, backup encryption keys, or S3 credentials. Inject them with an orchestrator secret store. Administrator and geo secrets must be independent. Generate a backup AES-256 key with `openssl rand -base64 32` and retain old keys for the lifetime of backups encrypted with them.

The public data API has no built-in end-user identity or row-level authorization. In production, enforce TLS, authentication, authorization, and trusted `X-SlimVector-Client-Id` injection at an ingress. Configure the same strong `Api:AdminApiKey` on every cluster node: besides protecting enabled administrator routes, it enables HMAC signatures on internal data, query, and catalog RPC. See [security](security.md).
