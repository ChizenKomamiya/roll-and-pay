using System.Net;
using System.Text.Json;
using Microsoft.Azure.Cosmos;
using RollAndPay.Api.Data;
using RollAndPay.Api.Games;
using RollAndPay.Api.Http;
using RollAndPay.Seed;
using Xunit;

namespace RollAndPay.Api.Tests;

public sealed class GameDiceIntegrationTests
{
    private sealed class Ones : IDie { public int Roll() => 1; }
    private static GameDice Service(Container container, ICosmosBatchWriter? writer = null) => new(new CosmosPointReader(container), writer ?? new CosmosBatchWriter(container), new Ones());
    private static Task<CommandResponse> Execute(GameDice dice, string owner, Guid id, Guid key, string etag, bool next, ReadSession session)
    {
        using var json = JsonDocument.Parse(next ? "{\"previousRoundNumber\":1}" : "{\"roundNumber\":1,\"mode\":\"remaining\"}");
        return dice.ExecuteAsync(owner, id, key, UpdateRequest.Hash("POST", $"/api/v1/games/{id:D}/{(next ? "rounds" : "rolls")}", json.RootElement, etag),
            etag, new(1, next ? "next" : "remaining"), next, session, default);
    }
    private static async Task<(Guid Id, string Etag, ReadSession Session)> Create(Container container, string owner, bool next)
    {
        await new InitialDataWriter(container).CreateAsync(InitialData.Build(owner, [DataTests.Person(), DataTests.Person("p002")], DateTimeOffset.UtcNow));
        var reader = new CosmosPointReader(container); var session = GameReaderTests.Session();
        var created = await new GameCreator(reader, new CosmosBatchWriter(container), new GameReader(reader)).CreateAsync(owner, Guid.NewGuid(),
            GameCreateTests.Hash(), ["p002", "p001"], session, default);
        if (!next) return (created.Command.GameId, created.Command.ResponseEtag, session);
        var tie = await Execute(Service(container), owner, created.Command.GameId, Guid.NewGuid(), created.Command.ResponseEtag, false, session);
        return (created.Command.GameId, tie.Command.ResponseEtag, session);
    }

    [EmulatorFact]
    [Trait("Category", "CosmosIntegration")]
    public async Task Concurrent_rolls_and_next_rounds_commit_once_and_same_keys_replay_with_a_fresh_client()
    {
        using var client = CosmosIntegrationTests.Client();
        var database = await client.CreateDatabaseAsync($"rollpay-g06-test-{Guid.NewGuid():N}");
        try
        {
            var container = (await database.Database.CreateContainerAsync("app", "/ownerKey")).Container;
            foreach (var next in new[] { false, true })
            foreach (var same in new[] { false, true })
            {
                var owner = OwnerKey.FromIds(Guid.NewGuid(), Guid.NewGuid()); var setup = await Create(container, owner, next);
                var key = Guid.NewGuid(); var arrived = 0; var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var writer = new HookWriter(new CosmosBatchWriter(container), async _ =>
                {
                    if (Interlocked.Increment(ref arrived) == 2) ready.TrySetResult();
                    await ready.Task.WaitAsync(TimeSpan.FromSeconds(8));
                });
                async Task<object> Run(Guid operationKey)
                {
                    var session = GameReaderTests.Session(); session.Accept(setup.Session.Token!);
                    try { return await Execute(Service(container, writer), owner, setup.Id, operationKey, setup.Etag, next, session); }
                    catch (ApiException e) { return e; }
                }
                var results = await Task.WhenAll(Run(key), Run(same ? key : Guid.NewGuid()));
                var successes = results.OfType<CommandResponse>().ToArray(); Assert.Equal(same ? 2 : 1, successes.Length);
                if (same) Assert.Single(successes, r => r.Replayed);
                else Assert.Equal(412, Assert.Single(results.OfType<ApiException>()).StatusCode);
                var saved = successes[0]; var command = await container.ReadItemAsync<CommandDocument>(saved.Command.Id, new(owner));
                using var fresh = CosmosIntegrationTests.Client(); var freshContainer = fresh.GetContainer(database.Database.Id, "app");
                var inherited = GameReaderTests.Session(); inherited.Accept(command.Headers.Session);
                Assert.True((await Execute(Service(freshContainer), owner, setup.Id, Guid.Parse(saved.Command.Id[8..]), setup.Etag, next, inherited)).Replayed);
                var game = await new GameReader(new CosmosPointReader(freshContainer)).GetAsync(owner, setup.Id, inherited, default);
                Assert.Equal(next ? 3 : 2, game.Revision); Assert.Equal(next ? "rolling" : "tie", game.Status);
                var original = await container.ReadItemAsync<RoundDocument>($"round:{setup.Id:D}:1", new(owner));
                Assert.Equal("tied", original.Resource.Status);
                if (next) Assert.All(game.CurrentRound.Rolls, r => Assert.Null(r.Value));
            }
        }
        finally { await database.Database.DeleteAsync(); }
    }

    [EmulatorFact]
    [Trait("Category", "CosmosIntegration")]
    public async Task Stale_round_or_game_condition_rolls_back_every_dice_or_next_round_write()
    {
        using var client = CosmosIntegrationTests.Client();
        var database = await client.CreateDatabaseAsync($"rollpay-g06-test-{Guid.NewGuid():N}");
        try
        {
            var container = (await database.Database.CreateContainerAsync("app", "/ownerKey")).Container;
            foreach (var next in new[] { false, true })
            {
                var owner = OwnerKey.FromIds(Guid.NewGuid(), Guid.NewGuid()); var setup = await Create(container, owner, next);
                var key = Guid.NewGuid();
                var writer = new HookWriter(new CosmosBatchWriter(container), async writes =>
                {
                    // Force a real 412 on the first operation (roll) or after the new-round Create (A07).
                    var write = next ? writes[1] : writes[0];
                    if (next)
                    {
                        var original = await container.ReadItemAsync<GameDocument>(write.Id, new(owner));
                        await container.ReplaceItemAsync(original.Resource with { Revision = 20 }, write.Id, new(owner));
                    }
                    else
                    {
                        var original = await container.ReadItemAsync<RoundDocument>(write.Id, new(owner));
                        await container.ReplaceItemAsync(original.Resource, write.Id, new(owner));
                    }
                });
                var error = await Assert.ThrowsAsync<ApiException>(() => Execute(Service(container, writer), owner, setup.Id, key, setup.Etag, next, setup.Session));
                Assert.Equal(next ? 412 : 503, error.StatusCode);
                var missing = await Assert.ThrowsAsync<CosmosException>(() => container.ReadItemAsync<CommandDocument>($"command:{key:D}", new(owner)));
                Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
                if (next)
                {
                    var noRound = await Assert.ThrowsAsync<CosmosException>(() => container.ReadItemAsync<RoundDocument>($"round:{setup.Id:D}:2", new(owner)));
                    Assert.Equal(HttpStatusCode.NotFound, noRound.StatusCode);
                }
                else
                {
                    var game = await new GameReader(new CosmosPointReader(container)).GetAsync(owner, setup.Id, setup.Session, default);
                    Assert.Equal(1, game.Revision); Assert.All(game.CurrentRound.Rolls, r => Assert.Null(r.Value));
                }
            }
        }
        finally { await database.Database.DeleteAsync(); }
    }

    [EmulatorFact]
    [Trait("Category", "CosmosIntegration")]
    public async Task Committed_roll_and_next_round_with_lost_response_replay_without_another_batch()
    {
        using var client = CosmosIntegrationTests.Client();
        var database = await client.CreateDatabaseAsync($"rollpay-g06-test-{Guid.NewGuid():N}");
        try
        {
            var container = (await database.Database.CreateContainerAsync("app", "/ownerKey")).Container;
            foreach (var next in new[] { false, true })
            {
                var owner = OwnerKey.FromIds(Guid.NewGuid(), Guid.NewGuid()); var setup = await Create(container, owner, next);
                var lost = new LostResponse(new CosmosBatchWriter(container)); var key = Guid.NewGuid();
                var response = await Execute(Service(container, lost), owner, setup.Id, key, setup.Etag, next, setup.Session);
                Assert.True(response.Replayed); Assert.Equal(1, lost.Commits);
                var again = await Execute(Service(container, lost), owner, setup.Id, key, setup.Etag, next, setup.Session);
                Assert.True(again.Replayed); Assert.Equal(1, lost.Commits);
                Assert.Equal(response.Command.ResponseBody.GetRawText(), again.Command.ResponseBody.GetRawText());
            }
        }
        finally { await database.Database.DeleteAsync(); }
    }

    private sealed class HookWriter(ICosmosBatchWriter inner, Func<IReadOnlyList<BatchWrite>, Task> hook) : ICosmosBatchWriter
    {
        public async Task<BatchResult> WriteAsync(string owner, IReadOnlyList<BatchWrite> writes, string? sessionToken, CancellationToken token)
        { await hook(writes); return await inner.WriteAsync(owner, writes, sessionToken, token); }
    }
    private sealed class LostResponse(ICosmosBatchWriter inner) : ICosmosBatchWriter
    {
        internal int Commits;
        public async Task<BatchResult> WriteAsync(string owner, IReadOnlyList<BatchWrite> writes, string? sessionToken, CancellationToken token)
        {
            var result = await inner.WriteAsync(owner, writes, sessionToken, token);
            Assert.False(result.Conflict); Commits++; throw CosmosErrors.Unavailable();
        }
    }
}
