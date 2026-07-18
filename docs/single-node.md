# Single-node operation

Single-node mode is the default. It uses the same deterministic command applier and storage engine as cluster mode but commits directly without opening Raft listeners.

## Local process

```bash
ASPNETCORE_URLS=http://localhost:8080 \
Storage__Path=./data \
Raft__Mode=SingleNode \
dotnet run --project src/SlimVector.Api -c Release
```

Or use Docker:

```bash
docker compose -f compose.single-node.yml up --build
```

The `/data` volume is the only required durable volume. Stop the process gracefully when possible; crash recovery is nevertheless tested through immutable segment replay and manifest reconciliation.

## Consistency

There is no network quorum. Leader and linearizable barriers both complete against the local state machine; stale reads read the same local committed state. The distinction is preserved in the API so applications can move unchanged to cluster mode.

## Memory lifecycle

Collections are catalog entries, not permanent in-memory objects. The first document operation or query opens a runtime and cold-loads its indexes. The eviction hosted service removes idle runtimes after `Collections:IdleTimeout`. `MaximumOpenCollections` bounds memory; if no inactive collection can be evicted, opening another collection returns controlled saturation instead of unbounded growth.

## Durability recommendation

Keep `Storage:FlushToDisk=true` outside tests. A single node has no replica redundancy, so enable scheduled backups and monitor their last-success timestamp. Single-node availability and data durability are limited by the host and volume; use cluster mode when a local host failure must not interrupt service.
