using System.Text.Json;
using Microsoft.Azure.Cosmos;

namespace RollAndPay.Api.Data;

public sealed record RosterRead(RosterDocument Document, string SessionToken, string Etag);
public interface IRosterStore
{
    Task<RosterRead> ReadAsync(string ownerKey, string? sessionToken, CancellationToken cancellationToken);
}

public sealed class CosmosRosterStore : IRosterStore
{
    private readonly Func<Container> container;
    public CosmosRosterStore(CosmosConnection connection) => container = () => connection.Container;
    public CosmosRosterStore(Container container) => this.container = () => container;

    public async Task<RosterRead> ReadAsync(string ownerKey, string? sessionToken, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var response = await container().ReadItemAsync<RosterDocument>("roster", new PartitionKey(ownerKey),
                new ItemRequestOptions { SessionToken = sessionToken, ConsistencyLevel = ConsistencyLevel.Session }, deadline.Token);
            if (!RosterValidation.IsValid(response.Resource, ownerKey)) throw CosmosErrors.Unavailable();
            return new(response.Resource, response.Headers.Session, response.ETag);
        }
        catch (CosmosException exception) { throw CosmosErrors.Translate(exception, sessionToken is not null); }
        catch (JsonException) { throw CosmosErrors.Unavailable(); }
        catch (ArgumentException) { throw sessionToken is null ? CosmosErrors.Unavailable() : Http.ReadSession.Invalid(); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw CosmosErrors.Unavailable(); }
        catch (HttpRequestException) { throw CosmosErrors.Unavailable(); }
        catch (Azure.Identity.AuthenticationFailedException) { throw CosmosErrors.Unavailable(); }
    }
}
