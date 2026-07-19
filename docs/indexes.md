# Indexes and search

## Vector indexes

SlimVector keeps a collection whole on one Raft data group; it does not shard an individual collection. Every vector index implements the same mutation, filtered-search, persistence, and rebuild contract.

| Kind | Search | Persistence and updates | Best fit |
| --- | --- | --- | --- |
| `flat` | Exact bounded top-K using `System.Numerics.Vector` for cosine, dot product, and L2 | Combined index snapshot; online upsert/delete | Small sets or exact recall |
| `hnsw` | Deterministic multi-layer graph with configurable `M`, construction/search breadth | Persistent graph, logical deletes, deterministic rebuild | Read-heavy medium sets in RAM |
| `ivfFlat` | Deterministic trained centroids, postings, configurable `nprobe` | Versioned centroid/posting snapshot; online delta mutations | Large sets where full vectors fit |
| `ivfPq` | IVF over residual product-quantization codes, followed by exact reranking | Versioned IVF/PQ codebooks and codes | Large, memory-sensitive sets |
| `diskAnn` | Vamana-style graph traversed from fixed SSD records with bounded beam/search list | Checksummed immutable generations, bounded LRU page cache, in-memory delta, merge threshold, rollback | Very large sets with limited RAM |

IVF training uses a fixed seed and deterministic ordering so replicas and repeatable benchmarks build equivalent codebooks. IVF-PQ requires `dimension % pqSubvectorCount == 0`. Approximate results should always be evaluated against Flat on a representative workload; `efSearch`, `nprobe`, rerank multiplier, and DiskANN search-list size exchange latency for recall.

Scalar quantization is selected independently with `quantization: float32|float16|int8` where supported. Flat stores real Float16 or calibrated per-dimension Int8 candidate data and reranks a configurable candidate set with originals. IVF-PQ supplies its own compression. HNSW and IVF-Flat currently retain Float32 graph/posting vectors; asking for scalar quantization does not silently change their algorithm.

## Automatic index generations

`kind: auto` continuously assesses vector count and dimension, estimated resident memory versus available memory, read/update rates, deletion ratio, observed query latency, configured recall floor, SSD suitability, and rebuild scale. The allowed set, thresholds, high-churn penalty, memory ratio, and validation policy are typed under `AutoIndex`.

An online transition is generation based:

1. keep the active index serving reads and writes;
2. snapshot source documents and train/build a candidate off the request path;
3. replay deletions and every current document into the candidate;
4. compare count, sampled recall against exact Flat, and measured latency gain;
5. write the generation snapshot, then atomically replace the checksummed manifest;
6. retain and continuously update the previous generation for immediate rollback.

The manifest stores active/previous generation numbers and kinds. Both generations are restored after process restart, so rollback remains available after later writes. A failed or rejected candidate never replaces the active manifest. Hysteresis, minimum migration interval, minimum recall, and minimum performance gain prevent oscillation.

Inspect and roll back with the authenticated endpoints:

```bash
curl -H "X-SlimVector-Admin-Key: $SLIMVECTOR_ADMIN_KEY" \
  http://localhost:8080/api/v1/admin/collections/articles/index

curl -X POST -H "X-SlimVector-Admin-Key: $SLIMVECTOR_ADMIN_KEY" \
  http://localhost:8080/api/v1/admin/collections/articles/index/rollback
```

## Text, metadata, and hybrid fusion

BM25 persists term frequencies, postings, document lengths, and corpus length. Unicode letter/digit tokenization and invariant casing are deterministic. `TextIndex:MaximumTermsPerDocument` rejects oversized documents before consensus.

Metadata uses existence and typed equality postings plus ordered numeric/date structures. Arrays contribute each scalar value; boolean expressions use posting intersection, union, and complement. With `MetadataIndex:IndexByDefault=false`, semantics remain correct through a document scan at O(document count).

Vector distance and BM25 score have different scales. Hybrid search ranks each independently and applies weighted reciprocal-rank fusion. Metadata filters produce a candidate id set first; every vector kind honors that constraint.

## Derived-data recovery

Combined snapshots include a document signature and settings. A stale/incompatible derived snapshot is rebuilt from source segments. Storage wrapper/checksum corruption is surfaced rather than hidden. DiskANN separately verifies generation headers and SHA-256 manifests. Source documents remain authoritative for every index.
