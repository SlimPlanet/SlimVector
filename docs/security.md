# Security

SlimVector separates public application traffic, administrator operations, internal cluster RPC, Raft transport, geographic replication, persistent data, and backups. A production deployment must enforce the boundaries below at the network and orchestration layers.

## Authentication and authorization boundaries

| Surface | Built-in protection | Operator responsibility |
| --- | --- | --- |
| `/api/v1/collections/*` | Payload validation, request limits, admission control; no end-user identity or row-level authorization | TLS, client authentication, authorization, tenant isolation, and quotas at an API gateway or service mesh |
| `/api/v1/admin/*` | Routes are absent unless enabled; constant-time `X-SlimVector-Admin-Key` validation | Use a unique secret of at least 32 characters, restrict the network, and audit access |
| `/internal/data/*` and `/internal/catalog/*` | HMAC-SHA256 payload signatures when `Api:AdminApiKey` is configured | Always configure the key in cluster mode, use mutually trusted private networking, and add mTLS or equivalent transport identity |
| Raft TCP ports | Consensus protocol and membership checks | Private network only, host firewall rules, stable node identity, and encrypted tunnels when the network is not trusted |
| `/internal/geo/replicate` | HMAC-SHA256 using `GeoReplication:SharedSecret`, strict sequence, deduplication, and divergence detection | TLS/mTLS between sites, a secret distinct from the administrator key, and explicit promotion fencing |
| Backup objects | SHA-256 integrity and optional AES-256-GCM per object | Independent 32-byte key, secret retention/rotation policy, least-privilege filesystem or S3 access, and restore drills |

HMAC authenticates content; it does not encrypt traffic. Use HTTPS or a mutually authenticated private transport for HTTP surfaces. Do not rely on secrecy of an internal IP address.

## Public API hardening

- Expose only the public API prefix and required health endpoints through the ingress. Keep `/metrics` private or protect it at the gateway.
- Apply an authenticated client identity before requests reach SlimVector. `X-SlimVector-Client-Id` affects fairness and rate-limit scope; it is not authentication and must be set or overwritten by a trusted proxy.
- Keep Kestrel's `Api:MaximumRequestBodyBytes`, `MaximumBatchSize`, request timeout, filter depth, maximum search limit, and write queues bounded.
- JSON and MessagePack accept untrusted input. MessagePack uses generated contracts with `UntrustedData`; typeless and contractless deserialization are disabled.
- Return Problem Details to clients but keep detailed internal exceptions out of production responses. Correlate with `traceId` in structured logs.

## Administrator access

Enable administrator routes only when an operator workflow needs them:

```bash
Api__AdminEndpointsEnabled=true
Api__AdminApiKey="$SLIMVECTOR_ADMIN_KEY"
```

The key must contain at least 32 characters. Inject it from a secret store; do not place it in a committed appsettings file, shell history, image layer, or URL. Rotate it as a coordinated cluster change because it also signs internal data and query RPC when configured. During rotation, update all nodes within a controlled maintenance window so they agree on the signer.

Administrator authorization is a single shared operational boundary, not per-user RBAC. Put the routes behind stronger identity-aware access control and audit logging when multiple operators or automation systems use them.

## Data and log privacy

Authoritative segments, derived indexes, Raft logs/snapshots, geographic outboxes, and backups may all contain document content, vectors, or metadata. Protect their volumes and replicas with host-level encryption, file permissions, restricted service identities, and lifecycle controls.

SlimVector structured application logs avoid document text, vectors, and metadata. Do not enable ASP.NET request/response body logging, reverse-proxy body capture, or verbose packet logging in production. Metrics expose identifiers and operational sizes; treat collection and node names according to your information-classification policy.

Studio and the default ingestion library keep extraction and embedding local. The first model preparation contacts the pinned Hugging Face artifact URL unless `AutoDownloadModel=false`; pre-provision the cache for isolated deployments. Uploaded documents are untrusted input, so retain upload limits and run the Studio in a suitably isolated process/container.

## Secrets and backups

- `Api:AdminApiKey`, `GeoReplication:SharedSecret`, `Backup:EncryptionKey`, and S3 credentials must all be distinct where applicable.
- `Backup:EncryptionKey` is base64 encoding of exactly 32 random bytes. Generate it with `openssl rand -base64 32`.
- Retain old encryption keys until every backup encrypted with them has expired and a replacement-key restore drill succeeds.
- Grant the backup identity access only to its configured filesystem path or S3 bucket/prefix.
- Use HTTPS for S3 endpoints and verify system clock accuracy because Signature V4 is time-sensitive.

## Production checklist

1. Terminate TLS and authenticate clients before the public API.
2. Block public access to administrator, internal RPC, Raft, metrics, storage, and backup surfaces.
3. Configure a strong administrator key consistently on every cluster node.
4. Use separate failure-domain and geo secrets, plus an independent backup encryption key.
5. Place each node's data on its own encrypted durable volume.
6. Disable request-body logging and validate log/metric destinations.
7. Keep dependencies and the Native AOT image patched; run the repository's package audit and smoke test.
8. Rehearse backup restore, node drain, quorum failure, and DR promotion with production-equivalent controls.

See [configuration](configuration.md), [cluster operations](cluster.md), [backup and restore](backup-restore.md), and [geographic replication](geo-replication.md) for the associated runbooks.
