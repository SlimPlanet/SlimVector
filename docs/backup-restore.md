# Backup and restore

Backups are scheduled when `Backup:Enabled=true`; `Backup:Interval` and `RetentionCount` control cadence and retained manifests. Manual operations use the protected administrator API described in [API](api.md).

## Format

SlimVector creates one versioned MemoryPack payload per collection after a linearizable barrier. Plaintext is SHA-256 hashed and stored under a content-addressed blob key. An incremental manifest references unchanged hashes instead of uploading duplicate blobs and records its parent backup id.

With `Backup:Encrypt=true`, each object is wrapped independently with AES-256-GCM using a fresh nonce. The content key is configured as base64. Integrity verification decrypts/authenticates the envelope, validates the plaintext hash, deserializes the payload, and checks collection/document counts.

Backups are consistent per collection at its barrier. A backup spanning multiple collections is not a single cross-collection transaction; applications needing such a boundary must quiesce related writers or record their own logical epoch.

## Filesystem provider

```json
{
  "Backup": {
    "Enabled": true,
    "Provider": "FileSystem",
    "Path": "/backup-volume",
    "Interval": "06:00:00",
    "RetentionCount": 14,
    "Encrypt": true,
    "EncryptionKey": "<base64-32-byte-key>"
  }
}
```

Place the backup path on storage independent from the live data volume.

## S3-compatible provider

Configure `S3Endpoint`, `S3Bucket`, `S3Region`, `S3AccessKey`, `S3SecretKey`, and `Prefix`. Requests use AWS Signature Version 4. Content-addressed PUT uses a conditional create, so retry after a timeout is idempotent: an already completed object is reused, while an interrupted request retries the same immutable key. Transport failures receive bounded retries.

Grant only prefix-scoped list/get/put/delete permissions. Use HTTPS and a private endpoint when available. Object-store lifecycle rules must not delete blobs still referenced by retained manifests; let SlimVector's retention pass delete unreferenced blobs.

## Administration

```bash
export SLIMVECTOR_ADMIN_KEY='replace-with-at-least-32-characters'

curl -X POST http://localhost:8080/api/v1/admin/backups \
  -H "X-SlimVector-Admin-Key: $SLIMVECTOR_ADMIN_KEY"

curl http://localhost:8080/api/v1/admin/backups \
  -H "X-SlimVector-Admin-Key: $SLIMVECTOR_ADMIN_KEY"

curl -X POST http://localhost:8080/api/v1/admin/backups/20260718T1200000000000Z-abcd/verify \
  -H "X-SlimVector-Admin-Key: $SLIMVECTOR_ADMIN_KEY"
```

## Restore

A full restore verifies every object first, then deletes current collections and recreates the manifest's collections through consensus. It is destructive and should run with public writers stopped.

```bash
curl -X POST http://localhost:8080/api/v1/admin/backups/BACKUP_ID/restore \
  -H "X-SlimVector-Admin-Key: $SLIMVECTOR_ADMIN_KEY"
```

Restore one collection in place:

```bash
curl -X POST http://localhost:8080/api/v1/admin/backups/backup-id/restore-collection \
  -H "X-SlimVector-Admin-Key: $SLIMVECTOR_ADMIN_KEY" \
  -H 'Content-Type: application/json' \
  -d '{"collectionName":"articles","overwrite":true}'
```

Restore under a new name by setting `restoredName`; SlimVector assigns a new collection id. After restore, indexes cold-load from authoritative restored documents and are persisted on subsequent mutation.

## Retention and PITR

Retention first removes old manifests, then computes all hashes reachable from remaining manifests and deletes only unreferenced blobs. The manifest parent and ordered, versioned command model provide the extension point for future point-in-time recovery; this release restores backup boundaries and does not advertise arbitrary timestamp recovery.

Run restore drills regularly. A backup is not operationally useful until verification and restore have both succeeded in an isolated environment.
