# Indexes and search

## Vector indexes

Flat stores vectors contiguously and calculates cosine, dot-product, or Euclidean distance with `System.Numerics.Vector` acceleration. It performs exact search and maintains a bounded top-K heap, avoiding a full result sort.

HNSW is a deterministic hierarchical navigable small-world graph. Level selection derives from the document id, so replicas applying the same ordered commands produce the same graph. `HnswM`, `HnswEfConstruction`, and `HnswEfSearch` control connectivity, build recall, and query recall. Deletes mark graph nodes and entry points are repaired.

`auto` uses Flat below `VectorIndex:AutoHnswThreshold` and HNSW at or above it. A threshold crossing rebuilds the effective vector index and persists the new combined snapshot.

Distance values are lower-is-better internally. Dot product is represented as a distance transformation so the top-K machinery remains consistent.

## Text

BM25 stores term frequencies, document lengths, postings, and corpus length. Tokenization is Unicode letter/digit based and lowercases invariantly. `TextIndex:Bm25K1` and `Bm25B` are applied to scoring. Documents exceeding `MaximumTermsPerDocument` are rejected before consensus/storage, preventing a post-commit indexing failure.

## Metadata

Metadata is indexed by field existence, scalar equality key, and an ordered numeric/date structure. Array values contribute each scalar element. Comparisons apply to integral, floating-point, and date/time values; `in` unions equality postings. Boolean expression evaluation uses set intersection, union, and complement.

When `MetadataIndex:IndexByDefault=false`, newly created collections store `MetadataIndexed=false`. Query semantics stay identical but filters scan their live document metadata. This is useful only for collections that rarely filter and should be chosen with the O(document count) cost understood.

The configured maximum expression depth is checked before evaluation. Equality is type-aware: integral `1`, floating-point `1.0`, text `"1"`, and boolean `true` are different keys.

## Hybrid fusion

BM25 scores and vector distances have incompatible scales. SlimVector independently ranks candidates and applies weighted reciprocal-rank fusion. `vectorWeight` and `textWeight` are each between 0 and 1 and cannot both be zero. `VectorIndex:HybridCandidateMultiplier` controls how many candidates each side contributes before the final top-K is selected.

## Filtering and candidates

Filters run first and produce an id set. Flat, HNSW, and BM25 accept that set as a candidate constraint. Metadata-only mode returns the filtered ids directly in stable ordinal order. An omitted filter produces the set of all live ids.

## Persistence and cold load

All four structures are written to `search-index-v1`. On cold load the document signature and settings must match exactly before the snapshot is used. Otherwise all structures are deterministically reconstructed from immutable segments. Metrics distinguish index loads, failures, Flat/HNSW loads, and HNSW cache hits/misses.
