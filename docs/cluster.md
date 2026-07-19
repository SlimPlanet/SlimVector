# Evolving Raft cluster

Cluster mode runs one catalog group and `Raft:DataGroupCount` independent data groups. A collection remains whole and maps deterministically to one data group. All groups use the catalog base port plus the group offset; with base port 3262 and two data groups, a node listens on 3262–3264.

## Bootstrap three voters

```bash
docker compose -f compose.cluster.yml config --quiet
docker compose -f compose.cluster.yml up --build
for port in 8081 8082 8083; do curl -fsS "http://localhost:$port/health/ready"; done
```

The nodes publish APIs on 8081–8083. A follower returns HTTP 307 to the group leader using `Raft:MemberApiEndpoints`. Production mappings must be stable client-visible HTTPS addresses.

## Add a fourth server safely

Start the supplied joiner profile. `Raft:JoinExistingCluster=true` deliberately starts every local group with an empty voting configuration: it listens for replication but cannot elect itself or write membership directly.

```bash
export SLIMVECTOR_ADMIN_KEY='replace-with-a-secret-at-least-32-characters'
docker compose -f compose.cluster.yml --profile joiner up --build -d
```

The Compose example enables administrator endpoints and injects this key into every node. Add the joiner's endpoint once for every group (catalog 3262, data groups 3263 and 3264):

```bash
for spec in 'catalog 3262' 'data-0 3263' 'data-1 3264'; do
  set -- $spec
  curl -fsS -X POST http://localhost:8081/api/v1/admin/cluster/membership/add \
    -H "X-SlimVector-Admin-Key: $SLIMVECTOR_ADMIN_KEY" \
    -H 'Content-Type: application/json' \
    -d "{\"groupId\":\"$1\",\"endpoint\":\"172.28.0.14:$2\"}"
done
```

DotNext keeps the endpoint outside the committed voter configuration while it transfers snapshots/log entries and completes the configured warm-up rounds. `AddMemberAsync` commits the new voter only after catch-up succeeds; `/promote` verifies that this safe promotion completed and is therefore idempotent. SlimVector never edits a live member list directly.

Query progress concurrently:

```bash
curl -H "X-SlimVector-Admin-Key: $SLIMVECTOR_ADMIN_KEY" \
  http://localhost:8081/api/v1/admin/cluster/membership
```

The response exposes each group, transport availability, leader, local applied/match index and lag where observable, plus `catching-up`, `demoting`, or `removing` state. Prometheus exposes member counts and membership-change gauges. Membership files are checksummed and atomically replaced per group; the committed transition also remains in the Raft WAL. Restarting a joined server reloads the consensus-installed configuration.

## Transfer, demote, and remove

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

Repeat for each data group. DotNext 6.x has no persistent learner role after a voter is committed, so SlimVector's explicit demotion is a serialized maintenance stage immediately preceding the consensus removal. Removal refuses the leader and any transition that would leave fewer than `ClusterMembership:MinimumVotingMembers`; unsafe/conflicting changes return HTTP 409 with `membership_conflict`.

## Failure and consistency behavior

A majority is required for writes and linearizable barriers. No quorum yields controlled 503 responses and adaptive admission pressure; requests are not acknowledged locally. `leader`, `linearizable`, and `stale` reads respectively use leader state, a strong quorum barrier, or local applied state. Stop/upgrade one voter at a time and wait for lag to reach zero. Never copy or share a live node volume.

Keep `DataGroupCount` stable after data exists: changing it alters placement and is not online collection sharding.
