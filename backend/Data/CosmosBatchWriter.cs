using System.Net;
using Microsoft.Azure.Cosmos;

namespace RollAndPay.Api.Data;

public sealed record BatchWrite(string Id, object Document, string? Etag = null);
public sealed record BatchResult(bool Conflict, string? SessionToken);
public interface ICosmosBatchWriter
{
    Task<BatchResult> WriteAsync(string owner, IReadOnlyList<BatchWrite> writes, string? sessionToken, CancellationToken token);
}

public sealed class CosmosBatchWriter : ICosmosBatchWriter
{
    private readonly Func<Container> container;
    public CosmosBatchWriter(CosmosConnection connection) => container = () => connection.Container;
    public CosmosBatchWriter(Container container) => this.container = () => container;

    public async Task<BatchResult> WriteAsync(string owner, IReadOnlyList<BatchWrite> writes, string? sessionToken, CancellationToken token)
    {
        try
        {
            var batch = container().CreateTransactionalBatch(new PartitionKey(owner));
            foreach (var write in writes)
            {
                if (write.Etag is null) batch.CreateItem(write.Document);
                else batch.ReplaceItem(write.Id, write.Document, new TransactionalBatchItemRequestOptions { IfMatchEtag = write.Etag });
            }
            using var result = await batch.ExecuteAsync(new TransactionalBatchRequestOptions
                { SessionToken = sessionToken, ConsistencyLevel = ConsistencyLevel.Session }, token);
            if (result.IsSuccessStatusCode) return new(false, result.Headers.Session);
            // 424 describes dependent operations; inspect the operation that actually failed.
            var failure = result.FirstOrDefault(r => (int)r.StatusCode >= 400 && (int)r.StatusCode != 424);
            var status = failure?.StatusCode ?? result.StatusCode;
            if (status is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed) return new(true, result.Headers.Session);
            if (status == HttpStatusCode.TooManyRequests)
                throw new Http.ApiException(429, "RATE_LIMITED", "時間をおいて再試行してください。",
                    (int)Math.Clamp(Math.Ceiling(failure?.RetryAfter.TotalSeconds ?? result.RetryAfter?.TotalSeconds ?? 1), 1, 3600));
            throw CosmosErrors.Unavailable();
        }
        catch (CosmosException e) when (e.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
        { return new(true, e.Headers?.Session); }
        catch (CosmosException e) { throw CosmosErrors.Translate(e, sessionToken is not null); }
        catch (HttpRequestException) { throw CosmosErrors.Unavailable(); }
        catch (Azure.Identity.AuthenticationFailedException) { throw CosmosErrors.Unavailable(); }
    }
}
