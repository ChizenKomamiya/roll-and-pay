using System.Text.Json;
using Microsoft.Azure.Cosmos;
using RollAndPay.Api.Data;
using RollAndPay.Api.History;
using RollAndPay.Api.Http;
using Xunit;

namespace RollAndPay.Api.Tests;

public sealed class HistoryIntegrationTests
{
    private static ContainerProperties Properties()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "database", "container-definition.json")))
            directory = directory.Parent;
        var path = Path.Combine(directory!.FullName, "database", "container-definition.json");
        using var definition = JsonDocument.Parse(File.ReadAllText(path));
        return new ContainerProperties("app", "/ownerKey")
        {
            IndexingPolicy = Newtonsoft.Json.JsonConvert.DeserializeObject<IndexingPolicy>(definition.RootElement.GetProperty("indexingPolicy").GetRawText())!
        };
    }

    [EmulatorFact]
    [Trait("Category", "CosmosIntegration")]
    public async Task Real_indexed_query_pages_same_timestamp_rows_and_preserves_saved_names_without_rounds()
    {
        using var client = CosmosIntegrationTests.Client();
        var database = await client.CreateDatabaseAsync($"rollpay-g09-test-{Guid.NewGuid():N}");
        try
        {
            var container = (await database.Database.CreateContainerAsync(Properties())).Container;
            var games = new[] { HistoryHttpTests.Saved(1), HistoryHttpTests.Saved(2), HistoryHttpTests.Saved(3),
                HistoryHttpTests.Saved(4) with { Status = "deleted", DeletedAt = GameFixture.Now.AddMinutes(3) },
                HistoryHttpTests.Saved(5) with { Status = "cancelled" }, HistoryHttpTests.Saved(6) with { OwnerKey = "other" } };
            var session = GameReaderTests.Session();
            foreach (var game in games)
            {
                var write = await container.CreateItemAsync(game, new PartitionKey(game.OwnerKey));
                if (game.OwnerKey == DataTests.Owner) session.Accept(write.Headers.Session);
            }
            // No round or roster documents exist; history must still be readable.
            using var fresh = CosmosIntegrationTests.Client();
            var freshContainer = fresh.GetContainer(database.Database.Id, "app");
            var reader = new HistoryReader(new CosmosHistoryStore(freshContainer), new CosmosPointReader(freshContainer));
            var first = await reader.ListAsync(DataTests.Owner, new(2, null), session, default);
            Assert.Equal(new[] { games[2].GameId, games[1].GameId }, first.Items.Select(i => i.GameId));
            var query = HistoryQueryTests.Parse($"?limit=2&cursor={first.NextCursor}");
            var second = await reader.ListAsync(DataTests.Owner, query, session, default);
            Assert.Equal(games[0].GameId, Assert.Single(second.Items).GameId); Assert.Null(second.NextCursor);
            var detail = await reader.DetailAsync(DataTests.Owner, games[0].GameId, session, default);
            Assert.Equal("当時の名前A", detail.Participants[0].DisplayName);
            var empty = await reader.ListAsync("empty-owner", new(20, null), GameReaderTests.Session(), default);
            Assert.Empty(empty.Items); Assert.Null(empty.NextCursor);
        }
        finally { await database.Database.DeleteAsync(); }
    }

    [EmulatorFact]
    [Trait("Category", "CosmosIntegration")]
    public async Task Deleted_cursor_boundary_does_not_skip_the_next_row_and_fresh_client_inherits_deletion_session()
    {
        using var client = CosmosIntegrationTests.Client();
        var database = await client.CreateDatabaseAsync($"rollpay-g09-test-{Guid.NewGuid():N}");
        try
        {
            var container = (await database.Database.CreateContainerAsync(Properties())).Container;
            var firstGame = HistoryHttpTests.Saved(2); var nextGame = HistoryHttpTests.Saved(1);
            await container.CreateItemAsync(firstGame, new PartitionKey(DataTests.Owner));
            var created = await container.CreateItemAsync(nextGame, new PartitionKey(DataTests.Owner));
            var session = GameReaderTests.Session(); session.Accept(created.Headers.Session);
            var reader = new HistoryReader(new CosmosHistoryStore(container), new CosmosPointReader(container));
            var first = await reader.ListAsync(DataTests.Owner, new(1, null), session, default);
            var deleted = await container.ReplaceItemAsync(firstGame with { Status = "deleted", Revision = 5, DeletedAt = firstGame.UpdatedAt },
                firstGame.Id, new PartitionKey(DataTests.Owner));
            using var fresh = CosmosIntegrationTests.Client();
            var freshContainer = fresh.GetContainer(database.Database.Id, "app");
            reader = new HistoryReader(new CosmosHistoryStore(freshContainer), new CosmosPointReader(freshContainer));
            session.Accept(deleted.Headers.Session);
            var second = await reader.ListAsync(DataTests.Owner, HistoryQueryTests.Parse($"?limit=1&cursor={first.NextCursor}"), session, default);
            Assert.Equal(nextGame.GameId, Assert.Single(second.Items).GameId); Assert.Null(second.NextCursor);
            var gone = await Assert.ThrowsAsync<ApiException>(() => reader.DetailAsync(DataTests.Owner, firstGame.GameId, session, default));
            Assert.Equal(410, gone.StatusCode);
            var foreign = await Assert.ThrowsAsync<ApiException>(() => reader.DetailAsync("other", firstGame.GameId, GameReaderTests.Session(), default));
            Assert.Equal(404, foreign.StatusCode);
        }
        finally { await database.Database.DeleteAsync(); }
    }
}
