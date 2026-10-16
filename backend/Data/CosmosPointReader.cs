using System.Net;
using System.Text.Json;
using Microsoft.Azure.Cosmos;

namespace RollAndPay.Api.Data;

public sealed record DocumentRead<T>(T? Document, string? SessionToken, string? Etag) where T : class;
public interface ICosmosPointReader
{
    Task<DocumentRead<T>> ReadAsync<T>(string ownerKey, string id, string? sessionToken, CancellationToken cancellationToken) where T : class;
}

public sealed class CosmosPointReader : ICosmosPointReader
{
    private readonly Func<Container> container;
    public CosmosPointReader(CosmosConnection connection) => container = () => connection.Container;
    public CosmosPointReader(Container container) => this.container = () => container;

    public async Task<DocumentRead<T>> ReadAsync<T>(string ownerKey, string id, string? sessionToken, CancellationToken cancellationToken) where T : class
    {
        try
        {
            var response = await container().ReadItemAsync<T>(id, new PartitionKey(ownerKey),
                new ItemRequestOptions { SessionToken = sessionToken, ConsistencyLevel = ConsistencyLevel.Session }, cancellationToken);
            if (response.Resource is null || string.IsNullOrEmpty(response.ETag) || string.IsNullOrEmpty(response.Headers.Session))
                throw CosmosErrors.Unavailable();
            return new(response.Resource, response.Headers.Session, response.ETag);
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound && exception.SubStatusCode == 0)
        {
            // A missing database/container is an availability failure, not a missing game.
            try { await container().ReadContainerAsync(cancellationToken: cancellationToken); }
            catch (CosmosException metadataError) { throw CosmosErrors.Translate(metadataError, false); }
            catch (HttpRequestException) { throw CosmosErrors.Unavailable(); }
            catch (Azure.Identity.AuthenticationFailedException) { throw CosmosErrors.Unavailable(); }
            return new(null, exception.Headers?.Session, null);
        }
        catch (CosmosException exception) { throw CosmosErrors.Translate(exception, sessionToken is not null); }
        catch (JsonException) { throw CosmosErrors.Unavailable(); }
        catch (ArgumentException) { throw sessionToken is null ? CosmosErrors.Unavailable() : Http.ReadSession.Invalid(); }
        catch (HttpRequestException) { throw CosmosErrors.Unavailable(); }
        catch (Azure.Identity.AuthenticationFailedException) { throw CosmosErrors.Unavailable(); }
    }
}
