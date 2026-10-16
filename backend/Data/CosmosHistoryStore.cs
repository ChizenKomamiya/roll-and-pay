using System.Globalization;
using System.Text.Json;
using Microsoft.Azure.Cosmos;
using RollAndPay.Api.History;

namespace RollAndPay.Api.Data;

public sealed record HistoryRead(IReadOnlyList<GameDocument> Games, string SessionToken);
public interface IHistoryStore
{
    Task<HistoryRead> QueryAsync(string owner, HistoryQuery query, string? sessionToken, CancellationToken cancellationToken);
}

public sealed class CosmosHistoryStore : IHistoryStore
{
    private readonly Func<Container> container;
    public CosmosHistoryStore(CosmosConnection connection) => container = () => connection.Container;
    public CosmosHistoryStore(Container container) => this.container = () => container;

    public async Task<HistoryRead> QueryAsync(string owner, HistoryQuery query, string? sessionToken, CancellationToken cancellationToken)
    {
        try
        {
            var boundary = query.Boundary is null ? "" :
                " AND (c.createdAt < @lastDate OR (c.createdAt = @lastDate AND c.gameId < @lastId))";
            // Project the saved game, never roster, rounds, or command responses.
            var definition = new QueryDefinition("SELECT TOP @take c.id, c.ownerKey, c.type, c.schemaVersion, " +
                "c.gameId, c.revision, c.status, c.createdAt, c.updatedAt, c.completedAt, c.cancelledAt, c.deletedAt, " +
                "c.currentRoundNumber, c.participants, c.loserParticipantId, c.payment FROM c " +
                "WHERE c.type = 'game' AND c.status = 'completed'" + boundary +
                " ORDER BY c.type ASC, c.status ASC, c.createdAt DESC, c.gameId DESC")
                .WithParameter("@take", query.Limit + 1);
            if (query.Boundary is { } before)
                definition.WithParameter("@lastDate", before.CreatedAt.ToString(HistoryQuery.DateFormat, CultureInfo.InvariantCulture))
                    .WithParameter("@lastId", before.GameId.ToString("D"));
            var options = new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(owner), ConsistencyLevel = ConsistencyLevel.Session,
                SessionToken = sessionToken, MaxItemCount = query.Limit + 1
            };
            using var iterator = container().GetItemQueryIterator<GameDocument>(definition, requestOptions: options);
            var games = new List<GameDocument>();
            string? latestSession = null;
            // Cosmos may return short or empty SDK pages before the logical page is full.
            while (iterator.HasMoreResults)
            {
                var page = await iterator.ReadNextAsync(cancellationToken);
                latestSession = page.Headers.Session;
                if (string.IsNullOrEmpty(latestSession)) throw CosmosErrors.Unavailable();
                options.SessionToken = latestSession;
                games.AddRange(page);
                if (games.Count > query.Limit + 1) throw CosmosErrors.Unavailable();
            }
            return new(games, latestSession ?? throw CosmosErrors.Unavailable());
        }
        catch (CosmosException exception) { throw CosmosErrors.Translate(exception, sessionToken is not null); }
        catch (JsonException) { throw CosmosErrors.Unavailable(); }
        catch (ArgumentException) { throw sessionToken is null ? CosmosErrors.Unavailable() : Http.ReadSession.Invalid(); }
        catch (HttpRequestException) { throw CosmosErrors.Unavailable(); }
        catch (Azure.Identity.AuthenticationFailedException) { throw CosmosErrors.Unavailable(); }
    }
}
