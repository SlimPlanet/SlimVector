# Troubleshooting

## Startup refuses configuration

Read the complete options-validation failure; SlimVector reports every inconsistency found in the section. For cluster failures, verify IP literals (not DNS names) in Raft TCP endpoints, inclusion of the local base endpoint, identical member order/count, one public API URI per member, and enough consecutive ports.

Secrets must meet length/encoding rules. Backup encryption requires exactly 32 decoded bytes, not a 32-character base64 string.

## `307 not_leader`

Allow 307 redirects without changing POST/PATCH to GET. If `Location` is absent, the leader was unknown or no public mapping matched its Raft IP. Check `Raft:Members`, `MemberApiEndpoints`, and leader metrics. Do not retry an unbounded loop; use jittered retries and honor 503/`Retry-After` during election.

## `503 quorum_unavailable` or readiness failure

Check process/network reachability on the catalog base port plus every data-group offset. A three-node group needs two reachable members. Look for repeated term changes and compare committed/applied indexes. Restore one failed member at a time; never point a new node at another node's live volume.

## HTTP 429

Read `X-SlimVector-RateLimit-Kind` and `X-SlimVector-RateLimit-Scope`, then honor `Retry-After`. A contractual refusal identifies global/client/collection/operation policy. A congestion refusal identifies queue, memory, Raft lag/quorum, or errors. Reduce producer concurrency or payload first. Increase limits only after confirming memory headroom; fix disk/network/quorum health rather than hiding it with larger queues.

## Auto migration is rejected or failed

Read the authenticated index status reason and migration metrics. `rejected` means count, sampled recall, or measured gain missed policy; the old generation remains active. `failed` means training/build/persistence failed and likewise does not switch the manifest. Check PQ divisibility, DiskANN path permissions/capacity, memory headroom, and validation settings. Rollback is available only after a successful generation switch.

## Membership change conflict

HTTP 409 `membership_conflict` means another change owns the group, removal violates `MinimumVotingMembers`, or the target is the current leader. Query membership status, wait for catch-up, transfer leadership when necessary, and operate one group at a time. A new joiner must use `JoinExistingCluster=true` with empty member arrays; never seed it as an independent one-node cluster.

## Slow query

Use the structured slow-query event to identify collection/mode/limit without exposing content. Check cold-load/index metrics, open-collection churn, filter selectivity, HNSW `efSearch`, hybrid candidate multiplier, limit, and memory pressure. A filtered HNSW query evaluates only its metadata candidate ids exactly; a very broad filter can therefore cost more than an unfiltered approximate query.

## Storage corruption

Stop the affected node and preserve its volume for diagnosis. Do not edit checksums/manifests. In a cluster, rebuild that replica from healthy peers/snapshot. In single-node mode restore a verified backup. A derived snapshot with a valid wrapper but stale inner signature rebuilds automatically; a failed wrapper checksum is surfaced because unexpected disk corruption must remain visible.

## Geographic lag or divergence

Growing pending events with failed attempts usually means endpoint, TLS, secret, or network failure. Sequence gaps resolve when the sender resumes the missing event. Divergence does not auto-resolve: fence writes, preserve both state directories, compare origin/sequence/event receipts, and choose the authoritative site before rebuilding the other.

## Backup failure

For filesystem backup, verify independent-volume capacity and permissions. For S3, verify clock accuracy, region, endpoint, bucket, prefix permissions, and HTTPS certificates; Signature V4 is time-sensitive. Never rotate/delete the encryption key until all backups needing it have expired and a new-key restore drill succeeds.

## Native AOT publish

Publish with an explicit RID and `--self-contained true`. Only the aggregate serializer warnings documented in ADR 0001 are scoped as non-errors. Any detailed SlimVector trim/AOT warning is a regression and should be fixed, not suppressed.
