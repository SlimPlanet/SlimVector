# Three-node cluster

Cluster mode runs one catalog Raft group and multiple independent data groups on every node. It requires at least three unique IP endpoints.

## Compose example

```bash
docker compose -f compose.cluster.yml config --quiet
docker compose -f compose.cluster.yml up --build
```

Nodes are available at `http://localhost:8081`, `:8082`, and `:8083`. Their catalog Raft base endpoints are static container IPs on port 3262; the two configured data groups use 3263 and 3264. Volumes are independent.

Wait until each node reports ready:

```bash
for port in 8081 8082 8083; do curl -fsS "http://localhost:$port/health/ready"; done
```

Send writes to any node. A follower returns HTTP 307 to the current group's leader using `Raft:MemberApiEndpoints`. For production, normally put a retry/redirect-aware load balancer in front and configure those entries as stable client-visible HTTPS addresses.

## Placement and concurrency

The catalog group owns definitions and placement. Collection ids map deterministically over `DataGroupCount`; all nodes calculate the same group. The adaptive scheduler batches only writes sharing a group into one shard command, while separate group workers can propose concurrently. Within a group, round-robin collection rotation prevents one busy collection from monopolizing the batch.

Changing `DataGroupCount` after data exists changes deterministic placement and is not an online resharding operation. Keep it stable until an explicit migration tool is used.

## Failure behavior

A group can elect a new leader after one node fails. A majority must be reachable to commit writes and linearizable reads. Without quorum, requests return `503 quorum_unavailable`; they are not acknowledged locally. A restarted follower replays its log or installs a state-machine snapshot, including idempotency history, then catches up.

Each group is independently available: a problem with one data group need not prevent unrelated shards from progressing, although catalog unavailability blocks catalog operations.

## Read consistency

- `leader`: requires the group leader and provides its committed view.
- `linearizable`: performs the strongest quorum-backed barrier before reading.
- `stale`: serves local applied state and can lag the leader.

Use stale reads only when the caller explicitly accepts bounded-by-replica-state staleness. Metrics expose leader, term, applied/committed indexes, and applied command count per group.

## Rolling operations

Stop and upgrade one node at a time, waiting for it to become ready and catch up before moving to the next. Do not stop two nodes in a three-member cluster. Keep member lists, group counts, and port offsets identical across nodes. Back up before changing storage formats or topology.
