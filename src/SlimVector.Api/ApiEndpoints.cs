using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SlimVector.Api.Contracts;
using SlimVector.Application;
using SlimVector.Application.Configuration;
using SlimVector.Domain;

namespace SlimVector.Api;

internal static class ApiEndpoints
{
    internal const string RequestTimeoutPolicyName = "SlimVector.Api";

    public static IEndpointRouteBuilder MapSlimVectorApi(this IEndpointRouteBuilder endpoints)
    {
        ApiOptions apiOptions = endpoints.ServiceProvider.GetRequiredService<IOptions<ApiOptions>>().Value;
        RouteGroupBuilder api = endpoints.MapGroup(apiOptions.RoutePrefix);
        api.WithRequestTimeout(RequestTimeoutPolicyName);

        RouteGroupBuilder collections = api.MapGroup("/collections").WithTags("Collections");
        collections.MapPost("/", CreateCollectionAsync)
            .WithName("CreateCollection")
            .Accepts<CreateCollectionRequest>("application/json", ApiSerialization.MessagePackMediaType)
            .Produces<CollectionResponse>(StatusCodes.Status201Created, "application/json", ApiSerialization.MessagePackMediaType);
        collections.MapPost("/get-or-create", GetOrCreateCollectionAsync)
            .WithName("GetOrCreateCollection")
            .Accepts<CreateCollectionRequest>("application/json", ApiSerialization.MessagePackMediaType)
            .Produces<CollectionResponse>(StatusCodes.Status200OK, "application/json", ApiSerialization.MessagePackMediaType);
        collections.MapGet("/", ListCollectionsAsync)
            .WithName("ListCollections")
            .Produces<CollectionListResponse>(StatusCodes.Status200OK, "application/json", ApiSerialization.MessagePackMediaType);
        collections.MapGet("/{name}", GetCollectionAsync)
            .WithName("GetCollection")
            .Produces<CollectionResponse>(StatusCodes.Status200OK, "application/json", ApiSerialization.MessagePackMediaType);
        collections.MapPatch("/{name}", UpdateCollectionAsync)
            .WithName("UpdateCollection")
            .Accepts<UpdateCollectionRequest>("application/json", ApiSerialization.MessagePackMediaType)
            .Produces<CollectionResponse>(StatusCodes.Status200OK, "application/json", ApiSerialization.MessagePackMediaType);
        collections.MapDelete("/{name}", DeleteCollectionAsync).WithName("DeleteCollection").Produces(StatusCodes.Status204NoContent);

        RouteGroupBuilder documents = collections.MapGroup("/{name}/documents").WithTags("Documents");
        documents.MapPost("/add", AddDocumentsAsync)
            .WithName("AddDocuments")
            .Accepts<DocumentBatchRequest>("application/json", ApiSerialization.MessagePackMediaType)
            .Produces<BatchMutationResponse>(StatusCodes.Status200OK, "application/json", ApiSerialization.MessagePackMediaType);
        documents.MapPost("/upsert", UpsertDocumentsAsync)
            .WithName("UpsertDocuments")
            .Accepts<DocumentBatchRequest>("application/json", ApiSerialization.MessagePackMediaType)
            .Produces<BatchMutationResponse>(StatusCodes.Status200OK, "application/json", ApiSerialization.MessagePackMediaType);
        documents.MapPatch("/", UpdateDocumentsAsync)
            .WithName("UpdateDocuments")
            .Accepts<DocumentUpdateBatchRequest>("application/json", ApiSerialization.MessagePackMediaType)
            .Produces<BatchMutationResponse>(StatusCodes.Status200OK, "application/json", ApiSerialization.MessagePackMediaType);
        documents.MapGet("/", GetDocumentsAsync)
            .WithName("GetDocuments")
            .Produces<DocumentListResponse>(StatusCodes.Status200OK, "application/json", ApiSerialization.MessagePackMediaType);
        documents.MapPost("/delete", DeleteDocumentsAsync)
            .WithName("DeleteDocuments")
            .Accepts<DocumentDeleteRequest>("application/json", ApiSerialization.MessagePackMediaType)
            .Produces<BatchMutationResponse>(StatusCodes.Status200OK, "application/json", ApiSerialization.MessagePackMediaType);
        documents.MapGet("/count", CountDocumentsAsync)
            .WithName("CountDocuments")
            .Produces<CountResponse>(StatusCodes.Status200OK, "application/json", ApiSerialization.MessagePackMediaType);
        documents.MapPost("/query", QueryAsync)
            .WithName("QueryDocuments")
            .Accepts<QueryRequest>("application/json", ApiSerialization.MessagePackMediaType)
            .Produces<QueryResponse>(StatusCodes.Status200OK, "application/json", ApiSerialization.MessagePackMediaType);
        return endpoints;
    }

    private static async Task<IResult> CreateCollectionAsync(
        NegotiatedBody<CreateCollectionRequest> body,
        ISlimVectorDatabase database,
        IOptions<ApiOptions> options,
        CancellationToken cancellationToken)
    {
        CreateCollectionRequest request = body.Value;
        CollectionDefinition definition = await database.CreateCollectionAsync(
            request.Name,
            request.Dimension,
            request.Metric ?? DistanceMetric.Cosine,
            request.VectorIndex,
            cancellationToken).ConfigureAwait(false);
        return ApiSerialization.Created(
            $"{options.Value.RoutePrefix}/collections/{Uri.EscapeDataString(definition.Name)}",
            ContractMapper.ToResponse(definition));
    }

    private static async Task<IResult> GetOrCreateCollectionAsync(
        NegotiatedBody<CreateCollectionRequest> body,
        ISlimVectorDatabase database,
        CancellationToken cancellationToken)
    {
        CreateCollectionRequest request = body.Value;
        CollectionDefinition definition = await database.GetOrCreateCollectionAsync(
            request.Name,
            request.Dimension,
            request.Metric ?? DistanceMetric.Cosine,
            request.VectorIndex,
            cancellationToken).ConfigureAwait(false);
        return ApiSerialization.Ok(ContractMapper.ToResponse(definition));
    }

    private static async Task<IResult> ListCollectionsAsync(ISlimVectorDatabase database, CancellationToken cancellationToken)
    {
        IReadOnlyList<CollectionDefinition> definitions = await database.ListCollectionsAsync(cancellationToken).ConfigureAwait(false);
        return ApiSerialization.Ok(new CollectionListResponse { Collections = definitions.Select(ContractMapper.ToResponse).ToArray() });
    }

    private static async Task<IResult> GetCollectionAsync(string name, ISlimVectorDatabase database, CancellationToken cancellationToken)
    {
        CollectionDefinition definition = await database.GetCollectionAsync(name, cancellationToken).ConfigureAwait(false);
        return ApiSerialization.Ok(ContractMapper.ToResponse(definition));
    }

    private static async Task<IResult> UpdateCollectionAsync(
        string name,
        NegotiatedBody<UpdateCollectionRequest> body,
        ISlimVectorDatabase database,
        CancellationToken cancellationToken)
    {
        UpdateCollectionRequest request = body.Value;
        CollectionDefinition definition = await database.UpdateCollectionAsync(
            name,
            request.Name,
            request.VectorIndex,
            cancellationToken).ConfigureAwait(false);
        return ApiSerialization.Ok(ContractMapper.ToResponse(definition));
    }

    private static async Task<IResult> DeleteCollectionAsync(string name, ISlimVectorDatabase database, CancellationToken cancellationToken)
    {
        await database.DeleteCollectionAsync(name, cancellationToken).ConfigureAwait(false);
        return TypedResults.NoContent();
    }

    private static Task<IResult> AddDocumentsAsync(
        string name,
        NegotiatedBody<DocumentBatchRequest> body,
        ISlimVectorDatabase database,
        IOptions<ApiOptions> options,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
        MutateDocumentsAsync(name, body.Value, DocumentMutationKind.Add, database, options.Value, httpContext, cancellationToken);

    private static Task<IResult> UpsertDocumentsAsync(
        string name,
        NegotiatedBody<DocumentBatchRequest> body,
        ISlimVectorDatabase database,
        IOptions<ApiOptions> options,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
        MutateDocumentsAsync(name, body.Value, DocumentMutationKind.Upsert, database, options.Value, httpContext, cancellationToken);

    private static async Task<IResult> UpdateDocumentsAsync(
        string name,
        NegotiatedBody<DocumentUpdateBatchRequest> body,
        ISlimVectorDatabase database,
        IOptions<ApiOptions> options,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        DocumentUpdateBatchRequest request = body.Value;
        ValidateBatch(request.Documents.Length, options.Value.MaximumBatchSize);
        DocumentMutation[] mutations = request.Documents.Select(static document => new DocumentMutation
        {
            Kind = DocumentMutationKind.Update,
            Id = document.Id,
            Patch = ContractMapper.ToPatch(document),
        }).ToArray();
        BatchMutationResult result = await database
            .MutateAsync(name, mutations, request.Atomic ?? true, GetClientId(httpContext), cancellationToken)
            .ConfigureAwait(false);
        return ApiSerialization.Ok(ContractMapper.ToResponse(result));
    }

    private static async Task<IResult> GetDocumentsAsync(
        string name,
        [FromQuery] string[]? ids,
        [FromQuery] int? offset,
        [FromQuery] int? limit,
        [FromQuery] string? continuationToken,
        ISlimVectorDatabase database,
        IOptions<ApiOptions> options,
        CancellationToken cancellationToken)
    {
        int effectiveOffset = offset ?? 0;
        int effectiveLimit = limit is null or 0 ? 100 : limit.Value;
        if (effectiveOffset > options.Value.MaximumDocumentOffset)
        {
            throw new DomainException(
                ErrorCodes.InvalidLimit,
                $"Offset pagination is limited to {options.Value.MaximumDocumentOffset}; use continuationToken for deeper pages.");
        }

        DocumentPage page = await database
            .GetDocumentPageAsync(name, ids, effectiveOffset, effectiveLimit, continuationToken, cancellationToken)
            .ConfigureAwait(false);
        return ApiSerialization.Ok(new DocumentListResponse
        {
            Documents = page.Documents.Select(ContractMapper.ToResponse).ToArray(),
            ContinuationToken = page.ContinuationToken,
        });
    }

    private static async Task<IResult> DeleteDocumentsAsync(
        string name,
        NegotiatedBody<DocumentDeleteRequest> body,
        ISlimVectorDatabase database,
        IOptions<ApiOptions> options,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        DocumentDeleteRequest request = body.Value;
        ValidateBatch(request.Ids.Length, options.Value.MaximumBatchSize);
        DocumentMutation[] mutations = request.Ids.Select(static id => new DocumentMutation
        {
            Kind = DocumentMutationKind.Delete,
            Id = id,
        }).ToArray();
        BatchMutationResult result = await database
            .MutateAsync(name, mutations, request.Atomic ?? true, GetClientId(httpContext), cancellationToken)
            .ConfigureAwait(false);
        return ApiSerialization.Ok(ContractMapper.ToResponse(result));
    }

    private static async Task<IResult> CountDocumentsAsync(
        string name,
        ISlimVectorDatabase database,
        CancellationToken cancellationToken)
    {
        long count = await database.CountDocumentsAsync(name, cancellationToken).ConfigureAwait(false);
        return ApiSerialization.Ok(new CountResponse { Count = count });
    }

    private static async Task<IResult> QueryAsync(
        string name,
        NegotiatedBody<QueryRequest> body,
        ISlimVectorDatabase database,
        CancellationToken cancellationToken)
    {
        QueryRequest request = body.Value;
        SearchResponse response = await database.SearchAsync(name, ContractMapper.ToSearchRequest(request), cancellationToken).ConfigureAwait(false);
        return ApiSerialization.Ok(ContractMapper.ToResponse(response));
    }

    private static async Task<IResult> MutateDocumentsAsync(
        string name,
        DocumentBatchRequest request,
        DocumentMutationKind kind,
        ISlimVectorDatabase database,
        ApiOptions options,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        ValidateBatch(request.Documents.Length, options.MaximumBatchSize);
        DocumentMutation[] mutations = request.Documents.Select(document => new DocumentMutation
        {
            Kind = kind,
            Id = document.Id,
            Document = ContractMapper.ToDocument(document),
        }).ToArray();
        BatchMutationResult result = await database
            .MutateAsync(name, mutations, request.Atomic ?? true, GetClientId(httpContext), cancellationToken)
            .ConfigureAwait(false);
        return ApiSerialization.Ok(ContractMapper.ToResponse(result));
    }

    private static void ValidateBatch(int count, int maximum)
    {
        if (count is < 1 || count > maximum)
        {
            throw new DomainException("invalid_batch_size", $"Batch size must be between 1 and {maximum}.");
        }
    }

    private static string GetClientId(HttpContext httpContext) =>
        httpContext.User.Identity?.Name ?? httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
}
