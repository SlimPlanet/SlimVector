# Shared-nothing cluster

Cluster mode uses a three-voter Raft catalog and dynamic RF3 data groups. A node starts only the groups whose replica sets contain its `NodeId`; group ports are allocated persistently from `Raft:DataPortRangeStart`/`DataPortRangeCount`. Collections use 1,024 immutable virtual shards and an epoch-versioned shard-to-group map.

With homogeneous disks, advertised capacity is approximately `(total raw bytes × 0.85) / 3`. Adding three equally sized nodes to a three-node RF3 cluster therefore doubles useful capacity. The planner targets 65%, treats 80% as a high watermark, places at least four group replicas per node, creates/splits around 32 GiB, and uses bytes, free space, load and zones rather than shard counts.

## Bootstrap three voters

```bash
docker compose -f compose.cluster.yml config --quiet
docker compose -f compose.cluster.yml up --build
for port in 8081 8082 8083; do curl -fsS "http://localhost:$port/health/ready"; done
```

The nodes publish APIs on 8081–8083. Every API accepts CRUD and search; it forwards an operation to the required data-group replicas internally. Public and internal endpoints must be stable, mutually reachable HTTPS addresses in production.

## Join a server and approve redistribution

Start the supplied joiner profile with a unique node id, zone, disk capacity, internal endpoint and persistent Raft port range. Registration does not move data.

```bash
export SLIMVECTOR_ADMIN_KEY='replace-with-a-secret-at-least-32-characters'
docker compose -f compose.cluster.yml --profile joiner up --build -d
```

The Compose example enables administrator endpoints and injects this key into every node. Register the node on a catalog API:

```bash
curl -fsS -X POST http://localhost:8081/api/v1/admin/cluster/nodes/join \
  -H "X-SlimVector-Admin-Key: $SLIMVECTOR_ADMIN_KEY" \
  -H 'Content-Type: application/json' \
  -d '{"nodeId":"node-4","apiEndpoint":"https://node-4:8080","internalEndpoint":"https://node-4:8080","raftHost":"10.0.0.14","zone":"zone-b","capacityBytes":1099511627776,"raftPortStart":3263,"raftPortCount":128,"roles":["api","data"]}'
```

Inspect the dry-run. It contains source/target replica sets, zones, estimated bytes, reasons and utilization before/after. Approve its identifier only after validating disk and failure-domain headroom:

```bash
curl -fsS -H "X-SlimVector-Admin-Key: $SLIMVECTOR_ADMIN_KEY" \
  http://localhost:8081/api/v1/admin/cluster/nodes/rebalance/plan

curl -fsS -X POST -H "X-SlimVector-Admin-Key: $SLIMVECTOR_ADMIN_KEY" \
  -H 'Content-Type: application/json' -d '{"planId":"<plan-id>"}' \
  http://localhost:8081/api/v1/admin/cluster/nodes/rebalance/approve

curl -fsS -H "X-SlimVector-Admin-Key: $SLIMVECTOR_ADMIN_KEY" \
  http://localhost:8081/api/v1/admin/cluster/nodes/topology
```

Each approved relocation is durable. The target starts the group as a joining member; Raft installs the snapshot and catches it up, the leader is transferred when necessary, then the old member is removed. Restarting the coordinator or changing the catalog leader resumes the recorded phase idempotently. The default coordinator permits one incoming and one outgoing relocation per node.

## Drain and remove

A draining node receives no new replicas. Mark it, generate a plan with `drainNodeId`, approve it, wait until topology reports no replica on that node, then delete it:

```bash
curl -fsS -X POST -H "X-SlimVector-Admin-Key: $SLIMVECTOR_ADMIN_KEY" \
  http://localhost:8081/api/v1/admin/cluster/nodes/node-4/drain
curl -fsS -H "X-SlimVector-Admin-Key: $SLIMVECTOR_ADMIN_KEY" \
  'http://localhost:8081/api/v1/admin/cluster/nodes/rebalance/plan?drainNodeId=node-4'
curl -fsS -X DELETE -H "X-SlimVector-Admin-Key: $SLIMVECTOR_ADMIN_KEY" \
  http://localhost:8081/api/v1/admin/cluster/nodes/node-4
```

## Expert group membership

Only one membership change can be active per group. Before removing a leader, transfer leadership from that leader's API:

```bash
curl -fsS -X POST http://localhost:8081/api/v1/admin/cluster/membership/transfer-leadership \
  -H "X-SlimVector-Admin-Key: $SLIMVECTOR_ADMIN_KEY" -H 'Content-Type: application/json' \
  -d '{"groupId":"catalog"}'
```

Stage demotion, then commit removal on the current leader:

```bash
for action in demote remove; do
  curl -fsS -X POST "http://localhost:8081/api/v1/admin/cluster/membership/$action" \
    -H "X-SlimVector-Admin-Key: $SLIMVECTOR_ADMIN_KEY" -H 'Content-Type: application/json' \
    -d '{"groupId":"catalog","endpoint":"172.28.0.14:3262"}'
done
```

These low-level routes remain for repair. Normal scale-out uses the node endpoints above. DotNext 6.x has no persistent learner role after a voter is committed, so SlimVector's explicit demotion is a serialized maintenance stage immediately preceding consensus removal. Removal refuses the leader and any transition that would leave fewer than `ClusterMembership:MinimumVotingMembers`; unsafe/conflicting changes return HTTP 409 with `membership_conflict`.

## Failure and consistency behavior

A majority is required for writes and linearizable barriers. No quorum yields controlled 503 responses and adaptive admission pressure; requests are not acknowledged locally. `leader`, `linearizable`, and `stale` reads respectively use leader state, a strong quorum barrier, or local applied state. Stop/upgrade one voter at a time and wait for lag to reach zero. Never copy or share a live node volume.

The catalog leader marks an active node unavailable when it has not reported storage/replica health for `DataPlacement:FailureReplacementDelay` (five minutes by default). If at least RF active data nodes remain, it immediately persists and approves replacement moves for replicas on that node; this RF repair is automatic and is distinct from capacity rebalancing, which still requires administrator approval. A recovered server must rejoin with the same stable identity before it can receive placements again.

Virtual shards move online between groups without changing their hash identity. Use the authenticated shard rebalancer API for a dry-run and approval:

```bash
curl -H "X-SlimVector-Admin-Key: $SLIMVECTOR_ADMIN_KEY" \
  "http://localhost:8081/api/v1/admin/cluster/rebalance/plan?drainDataGroupId=data-0"

curl -X POST -H "X-SlimVector-Admin-Key: $SLIMVECTOR_ADMIN_KEY" \
  -H 'Content-Type: application/json' \
  -d '{"planId":"<plan-id>"}' \
  http://localhost:8081/api/v1/admin/cluster/rebalance/approve
```

Approved moves persist in the collection placement and resume after controller or catalog-leader restart. Copying writes a checksummed snapshot checkpoint for exactly one virtual shard; catch-up records an ordered upsert/delete delta and verifies that replay reconstructs the live source checksum before cutover. A final checksum barrier sends a changed source back through catch-up instead of switching stale data. `Rebalancing:ManualApproval` defaults to `true`; pause, resume, status and explicit advance endpoints are available under `/admin/cluster/rebalance`.

Vector and metadata queries fan out once per represented physical group and merge deterministically. Text and hybrid queries first aggregate BM25 document count, total length, and term document frequencies, then score each group with those global statistics. Distributed list pagination uses an opaque continuation token; compatibility offsets are limited to avoid unbounded fan-out.
