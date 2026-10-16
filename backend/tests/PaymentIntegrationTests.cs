using System.Net;
using System.Text.Json;
using Microsoft.Azure.Cosmos;
using RollAndPay.Api.Data;
using RollAndPay.Api.Games;
using RollAndPay.Api.Http;
using RollAndPay.Api.Payments;
using RollAndPay.Seed;
using Xunit;

namespace RollAndPay.Api.Tests;

public sealed class PaymentIntegrationTests
{
    private static Task<CommandResponse> Save(Container container, string owner, Guid id, Guid key, string etag,
        ReadSession session, ICosmosBatchWriter? writer = null) => new PaymentSaver(new CosmosPointReader(container), writer ?? new CosmosBatchWriter(container))
        .SaveAsync(owner, id, key, PaymentSaveTests.Hash(etag: etag, id: id), etag, 3800, session, default);

    private static async Task<(Guid Id, string Etag, ReadSession Session)> Setup(Container container, string owner)
    {
        await new InitialDataWriter(container).CreateAsync(InitialData.Build(owner, [DataTests.Person(), DataTests.Person("p002")], DateTimeOffset.UtcNow));
        var reader = new CosmosPointReader(container); var session = GameReaderTests.Session();
        var created = await new GameCreator(reader, new CosmosBatchWriter(container), new GameReader(reader)).CreateAsync(owner, Guid.NewGuid(),
            GameCreateTests.Hash(), ["p002", "p001"], session, default);
        var id = created.Command.GameId; var etag = created.Command.ResponseEtag;
        using var body = JsonDocument.Parse("{\"roundNumber\":1,\"mode\":\"remaining\"}");
        var rolled = await new GameDice(reader, new CosmosBatchWriter(container), new SequenceDie(6, 1)).ExecuteAsync(owner, id, Guid.NewGuid(),
            UpdateRequest.Hash("POST", $"/api/v1/games/{id:D}/rolls", body.RootElement, etag), etag, new(1, "remaining"), false, session, default);
        return (id, rolled.Command.ResponseEtag, session);
    }

    [EmulatorFact]
    [Trait("Category", "CosmosIntegration")]
    public async Task Concurrent_saves_commit_one_increment_and_same_keys_replay_with_fresh_client_session()
    {
        using var client = CosmosIntegrationTests.Client();
        var database = await client.CreateDatabaseAsync($"rollpay-g08-test-{Guid.NewGuid():N}");
        try
        {
            var container = (await database.Database.CreateContainerAsync("app", "/ownerKey")).Container;
            foreach (var same in new[] { true, false })
            {
                var owner = OwnerKey.FromIds(Guid.NewGuid(), Guid.NewGuid()); var setup = await Setup(container, owner); var key = Guid.NewGuid();
                var arrived = 0; var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var writer = new HookWriter(new CosmosBatchWriter(container), async _ =>
                { if (Interlocked.Increment(ref arrived) == 2) ready.TrySetResult(); await ready.Task.WaitAsync(TimeSpan.FromSeconds(8)); });
                async Task<object> Run(Guid operationKey)
                {
                    var session = GameReaderTests.Session(); session.Accept(setup.Session.Token!);
                    try { return await Save(container, owner, setup.Id, operationKey, setup.Etag, session, writer); }
                    catch (ApiException e) { return e; }
                }
                var results = await Task.WhenAll(Run(key), Run(same ? key : Guid.NewGuid()));
                var successes = results.OfType<CommandResponse>().ToArray(); Assert.Equal(same ? 2 : 1, successes.Length);
                if (same) Assert.Single(successes, r => r.Replayed); else Assert.Equal(412, Assert.Single(results.OfType<ApiException>()).StatusCode);
                var committed = successes[0]; var command = await container.ReadItemAsync<CommandDocument>(committed.Command.Id, new(owner));
                using var fresh = CosmosIntegrationTests.Client(); var freshContainer = fresh.GetContainer(database.Database.Id, "app");
                var inherited = GameReaderTests.Session(); inherited.Accept(command.Headers.Session);
                var replay = await Save(freshContainer, owner, setup.Id, Guid.Parse(committed.Command.Id[8..]), setup.Etag, inherited);
                Assert.True(replay.Replayed); Assert.Equal(committed.Command.ResponseBody.GetRawText(), replay.Command.ResponseBody.GetRawText());
                var totals = await new LossTotalsReader(new CosmosPointReader(freshContainer)).GetAsync(owner, inherited, default);
                Assert.Equal("1800", totals.Participants.Single(p => p.ParticipantId == "p001").TotalLossYen);
                Assert.Equal("0", totals.Participants.Single(p => p.ParticipantId == "p002").TotalLossYen);
                Assert.Equal("completed", (await new GameReader(new CosmosPointReader(freshContainer)).GetAsync(owner, setup.Id, inherited, default)).Status);
                Assert.Null(await new GameReader(new CosmosPointReader(freshContainer)).GetActiveAsync(owner, inherited, default));
            }
        }
        finally { await database.Database.DeleteAsync(); }
    }

    [EmulatorFact]
    [Trait("Category", "CosmosIntegration")]
    public async Task Every_conditional_failure_rolls_back_game_control_totals_and_command()
    {
        using var client = CosmosIntegrationTests.Client(); var database = await client.CreateDatabaseAsync($"rollpay-g08-test-{Guid.NewGuid():N}");
        try
        {
            var container = (await database.Database.CreateContainerAsync("app", "/ownerKey")).Container;
            foreach (var target in new[] { "game", "control", "lossTotals", "command" })
            {
                var owner = OwnerKey.FromIds(Guid.NewGuid(), Guid.NewGuid()); var setup = await Setup(container, owner); var key = Guid.NewGuid();
                var writer = new HookWriter(new CosmosBatchWriter(container), async writes =>
                {
                    if (target == "game")
                    {
                        var game = await container.ReadItemAsync<GameDocument>($"game:{setup.Id:D}", new(owner));
                        await container.ReplaceItemAsync(game.Resource with { Revision = 20 }, game.Resource.Id, new(owner));
                    }
                    else if (target == "control")
                    {
                        var control = await container.ReadItemAsync<ControlDocument>("control", new(owner));
                        await container.ReplaceItemAsync(control.Resource, "control", new(owner));
                    }
                    else if (target == "lossTotals")
                    {
                        var totals = await container.ReadItemAsync<LossTotalsDocument>("lossTotals", new(owner));
                        await container.ReplaceItemAsync(totals.Resource, "lossTotals", new(owner));
                    }
                    else
                    {
                        var command = (CommandDocument)writes[^1].Document;
                        await container.CreateItemAsync(command with { RequestHash = new string('0', 64) }, new(owner));
                    }
                });
                var error = await Assert.ThrowsAsync<ApiException>(() => Save(container, owner, setup.Id, key, setup.Etag, setup.Session, writer));
                Assert.Equal(target == "game" ? 412 : target == "command" ? 409 : 503, error.StatusCode);
                var saved = await container.ReadItemAsync<GameDocument>($"game:{setup.Id:D}", new(owner));
                Assert.Equal("awaitingPayment", saved.Resource.Status); Assert.Null(saved.Resource.Payment);
                Assert.Equal(setup.Id, (await container.ReadItemAsync<ControlDocument>("control", new(owner))).Resource.ActiveGameId);
                Assert.All((await container.ReadItemAsync<LossTotalsDocument>("lossTotals", new(owner))).Resource.Totals, t => Assert.Equal("0", t.TotalLossYen));
                if (target != "command") Assert.Equal(HttpStatusCode.NotFound, (await Assert.ThrowsAsync<CosmosException>(() =>
                    container.ReadItemAsync<CommandDocument>($"command:{key:D}", new(owner)))).StatusCode);
            }
        }
        finally { await database.Database.DeleteAsync(); }
    }

    [EmulatorFact]
    [Trait("Category", "CosmosIntegration")]
    public async Task Save_and_cancel_races_keep_either_winner_without_partial_aggregate_changes()
    {
        using var client = CosmosIntegrationTests.Client(); var database = await client.CreateDatabaseAsync($"rollpay-g08-test-{Guid.NewGuid():N}");
        try
        {
            var container = (await database.Database.CreateContainerAsync("app", "/ownerKey")).Container;
            foreach (var saveWins in new[] { true, false })
            {
                var owner = OwnerKey.FromIds(Guid.NewGuid(), Guid.NewGuid()); var setup = await Setup(container, owner);
                async Task<CommandResponse> Cancel(ICosmosBatchWriter writer) => await new GameCanceller(new CosmosPointReader(container), writer).CancelAsync(owner,
                    setup.Id, Guid.NewGuid(), GameCancelTests.Hash(setup.Etag, setup.Id), setup.Etag, setup.Session, default);
                var hook = new HookWriter(new CosmosBatchWriter(container), async _ =>
                {
                    if (saveWins) await Save(container, owner, setup.Id, Guid.NewGuid(), setup.Etag, setup.Session);
                    else await Cancel(new CosmosBatchWriter(container));
                });
                var error = await Assert.ThrowsAsync<ApiException>(() => saveWins ? Cancel(hook) : Save(container, owner, setup.Id, Guid.NewGuid(), setup.Etag, setup.Session, hook));
                Assert.Equal(412, error.StatusCode);
                var game = await new GameReader(new CosmosPointReader(container)).GetAsync(owner, setup.Id, setup.Session, default);
                Assert.Equal(saveWins ? "completed" : "cancelled", game.Status);
                var totals = await new LossTotalsReader(new CosmosPointReader(container)).GetAsync(owner, setup.Session, default);
                Assert.Equal(saveWins ? "1800" : "0", totals.Participants.Single(p => p.ParticipantId == "p001").TotalLossYen);
                Assert.Null(await new GameReader(new CosmosPointReader(container)).GetActiveAsync(owner, setup.Session, default));
            }
        }
        finally { await database.Database.DeleteAsync(); }
    }

    [EmulatorFact]
    [Trait("Category", "CosmosIntegration")]
    public async Task Lost_commit_response_replays_once_without_adding_again()
    {
        using var client = CosmosIntegrationTests.Client(); var database = await client.CreateDatabaseAsync($"rollpay-g08-test-{Guid.NewGuid():N}");
        try
        {
            var container = (await database.Database.CreateContainerAsync("app", "/ownerKey")).Container;
            var owner = OwnerKey.FromIds(Guid.NewGuid(), Guid.NewGuid()); var setup = await Setup(container, owner); var key = Guid.NewGuid(); var commits = 0;
            var writer = new LostResponse(new CosmosBatchWriter(container), () => commits++);
            var response = await Save(container, owner, setup.Id, key, setup.Etag, setup.Session, writer);
            Assert.True(response.Replayed); Assert.True((await Save(container, owner, setup.Id, key, setup.Etag, setup.Session, writer)).Replayed);
            Assert.Equal(1, commits);
            Assert.Equal("1800", (await new LossTotalsReader(new CosmosPointReader(container)).GetAsync(owner, setup.Session, default))
                .Participants.Single(p => p.ParticipantId == "p001").TotalLossYen);
        }
        finally { await database.Database.DeleteAsync(); }
    }

    private sealed class HookWriter(ICosmosBatchWriter inner, Func<IReadOnlyList<BatchWrite>, Task> hook) : ICosmosBatchWriter
    {
        public async Task<BatchResult> WriteAsync(string owner, IReadOnlyList<BatchWrite> writes, string? sessionToken, CancellationToken token)
        { await hook(writes); return await inner.WriteAsync(owner, writes, sessionToken, token); }
    }
    private sealed class LostResponse(ICosmosBatchWriter inner, Action committed) : ICosmosBatchWriter
    {
        public async Task<BatchResult> WriteAsync(string owner, IReadOnlyList<BatchWrite> writes, string? sessionToken, CancellationToken token)
        { var result = await inner.WriteAsync(owner, writes, sessionToken, token); Assert.False(result.Conflict); committed(); throw CosmosErrors.Unavailable(); }
    }
}
