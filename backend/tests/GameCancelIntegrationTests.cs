using System.Net;
using Microsoft.Azure.Cosmos;
using RollAndPay.Api.Data;
using RollAndPay.Api.Games;
using RollAndPay.Api.Http;
using RollAndPay.Seed;
using Xunit;

namespace RollAndPay.Api.Tests;

public sealed class GameCancelIntegrationTests
{
    private static async Task Seed(Container container, string owner, string status = "rolling")
    {
        await new InitialDataWriter(container).CreateAsync(InitialData.Build(owner, [DataTests.Person(), DataTests.Person("p002")], GameFixture.Now));
        using var result = await container.CreateTransactionalBatch(new PartitionKey(owner))
            .CreateItem(GameFixture.Game(status) with { OwnerKey = owner })
            .CreateItem(GameFixture.Round(status) with { OwnerKey = owner })
            .ReplaceItem("control", GameFixture.Control(GameFixture.Id) with { OwnerKey = owner }).ExecuteAsync();
        Assert.True(result.IsSuccessStatusCode);
    }

    [EmulatorFact]
    [Trait("Category", "CosmosIntegration")]
    public async Task Cancellation_preserves_draws_totals_and_roster_and_replays_after_response_loss_on_a_fresh_client()
    {
        using var client = CosmosIntegrationTests.Client();
        var database = await client.CreateDatabaseAsync($"rollpay-g05-test-{Guid.NewGuid():N}");
        try
        {
            var container = (await database.Database.CreateContainerAsync("app", "/ownerKey")).Container;
            foreach (var status in new[] { "rolling", "tie", "awaitingPayment" })
            {
                var owner = OwnerKey.FromIds(Guid.NewGuid(), Guid.NewGuid()); await Seed(container, owner, status);
                var partition = new PartitionKey(owner);
                var round = await container.ReadItemAsync<RoundDocument>($"round:{GameFixture.Id:D}:1", partition);
                var roster = await container.ReadItemAsync<RosterDocument>("roster", partition);
                var totals = await container.ReadItemAsync<LossTotalsDocument>("lossTotals", partition);
                var reader = new CosmosPointReader(container); var session = GameReaderTests.Session(); var key = Guid.NewGuid();
                var lost = new LostResponseWriter(new CosmosBatchWriter(container));
                var saved = await new GameCanceller(reader, lost).CancelAsync(owner, GameFixture.Id, key, GameCancelTests.Hash(), GameCancelTests.Etag, session, default);
                Assert.True(saved.Replayed); Assert.Equal(1, lost.Commits); Assert.NotNull(session.Token);
                Assert.Equal(round.ETag, (await container.ReadItemAsync<RoundDocument>(round.Resource.Id, partition)).ETag);
                Assert.Equal(roster.ETag, (await container.ReadItemAsync<RosterDocument>("roster", partition)).ETag);
                Assert.Equal(totals.ETag, (await container.ReadItemAsync<LossTotalsDocument>("lossTotals", partition)).ETag);
                using var fresh = CosmosIntegrationTests.Client();
                var freshContainer = fresh.GetContainer(database.Database.Id, "app"); var freshReader = new CosmosPointReader(freshContainer);
                var inherited = GameReaderTests.Session(); inherited.Accept(session.Token!);
                var replay = await new GameCanceller(freshReader, new CosmosBatchWriter(freshContainer))
                    .CancelAsync(owner, GameFixture.Id, key, GameCancelTests.Hash(), GameCancelTests.Etag, inherited, default);
                Assert.True(replay.Replayed); Assert.Equal(saved.Command.ResponseBody.GetRawText(), replay.Command.ResponseBody.GetRawText());
                Assert.Null(await new GameReader(freshReader).GetActiveAsync(owner, inherited, default));
                var game = await new GameReader(freshReader).GetAsync(owner, GameFixture.Id, inherited, default);
                Assert.Equal("cancelled", game.Status); Assert.Equal(5, game.Revision);
                Assert.Equal(GameFixture.Game(status).LoserParticipantId, game.LoserParticipantId);
                Assert.Equal(GameFixture.Round(status).Rolls, game.CurrentRound.Rolls);
                var created = await new GameCreator(freshReader, new CosmosBatchWriter(freshContainer), new GameReader(freshReader))
                    .CreateAsync(owner, Guid.NewGuid(), GameCreateTests.Hash(), ["p002", "p001"], inherited, default);
                await new GameCanceller(freshReader, new CosmosBatchWriter(freshContainer))
                    .CancelAsync(owner, GameFixture.Id, key, GameCancelTests.Hash(), GameCancelTests.Etag, inherited, default);
                Assert.Equal(created.Command.GameId, (await new GameReader(freshReader).GetActiveAsync(owner, inherited, default))!.GameId);
            }
        }
        finally { await database.Database.DeleteAsync(); }
    }

    [EmulatorFact]
    [Trait("Category", "CosmosIntegration")]
    public async Task Simultaneous_cancels_commit_one_revision_and_command_with_same_key_replay_or_other_key_412()
    {
        using var client = CosmosIntegrationTests.Client();
        var database = await client.CreateDatabaseAsync($"rollpay-g05-test-{Guid.NewGuid():N}");
        try
        {
            var container = (await database.Database.CreateContainerAsync("app", "/ownerKey")).Container;
            foreach (var sameKey in new[] { true, false })
            {
                var owner = OwnerKey.FromIds(Guid.NewGuid(), Guid.NewGuid()); await Seed(container, owner);
                var key = Guid.NewGuid(); var arrived = 0;
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var writer = new HookWriter(new CosmosBatchWriter(container), async () =>
                {
                    if (Interlocked.Increment(ref arrived) == 2) release.TrySetResult();
                    await release.Task.WaitAsync(TimeSpan.FromSeconds(8));
                });
                var canceller = new GameCanceller(new CosmosPointReader(container), writer);
                async Task<object> Run(Guid operationKey)
                {
                    try { return await canceller.CancelAsync(owner, GameFixture.Id, operationKey, GameCancelTests.Hash(), GameCancelTests.Etag, GameReaderTests.Session(), default); }
                    catch (ApiException e) { return e; }
                }
                var results = await Task.WhenAll(Run(key), Run(sameKey ? key : Guid.NewGuid()));
                var success = results.OfType<CommandResponse>().ToArray();
                Assert.Equal(sameKey ? 2 : 1, success.Length);
                if (sameKey) Assert.Single(success, r => r.Replayed);
                else Assert.Equal(412, Assert.Single(results.OfType<ApiException>()).StatusCode);
                var partition = new PartitionKey(owner);
                Assert.Equal(5, (await container.ReadItemAsync<GameDocument>($"game:{GameFixture.Id:D}", partition)).Resource.Revision);
                Assert.Null((await container.ReadItemAsync<ControlDocument>("control", partition)).Resource.ActiveGameId);
                using var query = container.GetItemQueryIterator<int>(new QueryDefinition("SELECT VALUE COUNT(1) FROM c WHERE c.type = 'command'"),
                    requestOptions: new QueryRequestOptions { PartitionKey = partition });
                Assert.Equal(1, (await query.ReadNextAsync()).Single());
            }
        }
        finally { await database.Database.DeleteAsync(); }
    }

    [EmulatorFact]
    [Trait("Category", "CosmosIntegration")]
    public async Task Stale_game_or_control_etag_rolls_back_all_cancellation_writes()
    {
        using var client = CosmosIntegrationTests.Client();
        var database = await client.CreateDatabaseAsync($"rollpay-g05-test-{Guid.NewGuid():N}");
        try
        {
            var container = (await database.Database.CreateContainerAsync("app", "/ownerKey")).Container;
            foreach (var changeGame in new[] { true, false })
            {
                var owner = OwnerKey.FromIds(Guid.NewGuid(), Guid.NewGuid()); await Seed(container, owner);
                var partition = new PartitionKey(owner); var gameId = $"game:{GameFixture.Id:D}"; var key = Guid.NewGuid();
                var beforeGame = await container.ReadItemAsync<GameDocument>(gameId, partition);
                var beforeControl = await container.ReadItemAsync<ControlDocument>("control", partition);
                var writer = new HookWriter(new CosmosBatchWriter(container), async () =>
                {
                    if (changeGame) await container.ReplaceItemAsync(beforeGame.Resource with { Revision = 5 }, gameId, partition);
                    else await container.ReplaceItemAsync(beforeControl.Resource with { ActiveGameId = Guid.NewGuid() }, "control", partition);
                });
                var error = await Assert.ThrowsAsync<ApiException>(() => new GameCanceller(new CosmosPointReader(container), writer)
                    .CancelAsync(owner, GameFixture.Id, key, GameCancelTests.Hash(), GameCancelTests.Etag, GameReaderTests.Session(), default));
                Assert.Equal(changeGame ? 412 : 503, error.StatusCode);
                var game = await container.ReadItemAsync<GameDocument>(gameId, partition);
                var control = await container.ReadItemAsync<ControlDocument>("control", partition);
                Assert.Equal("rolling", game.Resource.Status); Assert.Null(game.Resource.CancelledAt);
                if (changeGame) Assert.Equal(beforeControl.ETag, control.ETag); else Assert.Equal(beforeGame.ETag, game.ETag);
                var missing = await Assert.ThrowsAsync<CosmosException>(() => container.ReadItemAsync<CommandDocument>($"command:{key:D}", partition));
                Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            }
        }
        finally { await database.Database.DeleteAsync(); }
    }

    private sealed class HookWriter(ICosmosBatchWriter inner, Func<Task> before) : ICosmosBatchWriter
    {
        public async Task<BatchResult> WriteAsync(string owner, IReadOnlyList<BatchWrite> writes, string? sessionToken, CancellationToken token)
        { await before(); return await inner.WriteAsync(owner, writes, sessionToken, token); }
    }

    private sealed class LostResponseWriter(ICosmosBatchWriter inner) : ICosmosBatchWriter
    {
        internal int Commits;
        public async Task<BatchResult> WriteAsync(string owner, IReadOnlyList<BatchWrite> writes, string? sessionToken, CancellationToken token)
        {
            var result = await inner.WriteAsync(owner, writes, sessionToken, token);
            Assert.False(result.Conflict); Commits++;
            throw CosmosErrors.Unavailable();
        }
    }
}
