namespace SlimVector.Domain;

public enum DistanceMetric
{
    Cosine,
    DotProduct,
    Euclidean,
}

public enum VectorIndexKind
{
    Auto,
    Flat,
    Hnsw,
}

public enum SearchMode
{
    Vector,
    Text,
    Hybrid,
    Metadata,
}

public enum ReadConsistency
{
    Leader,
    Linearizable,
    Stale,
}

[Flags]
public enum IncludeFields
{
    None = 0,
    Text = 1,
    Vector = 2,
    Metadata = 4,
    Scores = 8,
    All = Text | Vector | Metadata | Scores,
}

public enum MetadataValueKind
{
    Null,
    Text,
    Boolean,
    Integral,
    Number,
    DateTime,
    Identifier,
    TextArray,
    BooleanArray,
    IntegralArray,
    NumberArray,
}

public enum MetadataOperator
{
    Equal,
    NotEqual,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
    In,
    Exists,
    And,
    Or,
    Not,
}

public enum DocumentMutationKind
{
    Add,
    Upsert,
    Update,
    Delete,
}

public enum ExecutionMode
{
    SingleNode,
    Cluster,
}

public enum GeoReplicationMode
{
    Asynchronous,
    Synchronous,
}

public enum BackupProvider
{
    FileSystem,
    S3,
}
