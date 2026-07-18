namespace SlimVector.Indexing;

public readonly record struct RankedResult(string Id, double Score);

public readonly record struct HybridRankedResult(string Id, double Score, int? VectorRank, int? TextRank);
