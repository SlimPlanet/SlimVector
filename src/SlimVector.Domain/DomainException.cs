namespace SlimVector.Domain;

public sealed class DomainException : Exception
{
    public DomainException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

public static class ErrorCodes
{
    public const string CollectionAlreadyExists = "collection_already_exists";
    public const string CollectionNotFound = "collection_not_found";
    public const string DimensionMismatch = "dimension_mismatch";
    public const string DocumentAlreadyExists = "document_already_exists";
    public const string DocumentNotFound = "document_not_found";
    public const string InvalidCollectionName = "invalid_collection_name";
    public const string InvalidDimension = "invalid_dimension";
    public const string InvalidDocumentId = "invalid_document_id";
    public const string InvalidFilter = "invalid_filter";
    public const string InvalidIndexConfiguration = "invalid_index_configuration";
    public const string InvalidLimit = "invalid_limit";
    public const string InvalidMetadata = "invalid_metadata";
    public const string InvalidPlacement = "invalid_placement";
    public const string RoutingEpochMismatch = "routing_epoch_mismatch";
    public const string CrossShardAtomicUnsupported = "cross_shard_atomic_unsupported";
    public const string MembershipConflict = "membership_conflict";
    public const string MembershipMemberNotFound = "membership_member_not_found";
    public const string InvalidVector = "invalid_vector";
    public const string InvalidWeights = "invalid_weights";
    public const string QueueSaturated = "queue_saturated";
    public const string RequestTooLarge = "request_too_large";
    public const string ReadOnlySecondary = "read_only_secondary";
    public const string WriteTooLarge = "write_too_large";
    public const string StorageCorrupted = "storage_corrupted";
    public const string TextRequired = "text_required";
    public const string TextTooLarge = "text_too_large";
    public const string VectorRequired = "vector_required";
}
