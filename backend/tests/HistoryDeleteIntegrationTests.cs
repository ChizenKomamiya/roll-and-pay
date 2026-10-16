using System.Net;
using System.Text.Json;
using Microsoft.Azure.Cosmos;
using RollAndPay.Api.Data;
using RollAndPay.Api.Games;
using RollAndPay.Api.History;
using RollAndPay.Api.Http;
using RollAndPay.Api.Payments;
using RollAndPay.Seed;
using Xunit;

namespace RollAndPay.Api.Tests;

public sealed class HistoryDeleteIntegrationTests
{
    private static ContainerProperties Properties()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "database", "container-definition.json"))) directory = directory.Parent;
        using var definition = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory!.FullName, "database", "container-definition.json")));
        return new("app", "/ownerKey")
        {
            IndexingPolicy = Newtonsoft.Json.JsonConvert.DeserializeObject<IndexingPolicy>(definition.RootElement.GetProperty("indexingPolicy").GetRawText())!
        };
    }
    private static Task<CommandResponse> Delete(Container c, string owner, Guid id, Guid key, string etag, ReadSession session, ICosmosBatchWriter? writer = null) =>
        new HistoryDeleter(new CosmosPointReader(c), writer ?? new CosmosBatchWriter(c)).DeleteAsync(owner, id, key,
            HistoryDeleteTests.Hash(etag, id), etag, session, default);
    private static Task<CommandResponse> Save(Container c, string owner, Guid id, Guid key, ReadSession session, ICosmosBatchWriter? writer = null) =>
        new PaymentSaver(new CosmosPointReader(c), writer ?? new CosmosBatchWriter(c)).SaveAsync(owner, id, key,
            PaymentSaveTests.Hash(etag: GameEtag.Create(id, 4), id: id), GameEtag.Create(id, 4), 3800, session, default);
    private static async Task Seed(Container c, string owner) => await new InitialDataWriter(c).CreateAsync(
        InitialData.Build(owner, [DataTests.Person(), DataTests.Person("p002")], GameFixture.Now));
    private static async Task<Guid> Prepare(Container c, string owner)
    {
        var id = Guid.NewGuid();
        await c.CreateItemAsync(GameFixture.Game("awaitingPayment") with { Id = $"game:{id:D}", GameId = id, OwnerKey = owner }, new PartitionKey(owner));
        await c.CreateItemAsync(GameFixture.Round("awaitingPayment") with { Id = $"round:{id:D}:1", GameId = id, OwnerKey = owner }, new PartitionKey(owner));
        await c.ReplaceItemAsync(GameFixture.Control(id) with { OwnerKey = owner }, "control", new PartitionKey(owner));
        return id;
    }

    [EmulatorFact]
    [Trait("Category", "CosmosIntegration")]
    public async Task Simultaneous_same_history_deletes_commit_once_same_key_replays_other_key_is_410()
    {
        using var client = CosmosIntegrationTests.Client(); var db = await client.CreateDatabaseAsync($"rollpay-g10-test-{Guid.NewGuid():N}");
        try
        {
            var c = (await db.Database.CreateContainerAsync("app", "/ownerKey")).Container;
            foreach (var same in new[] { true, false })
            {
                var owner = OwnerKey.FromIds(Guid.NewGuid(), Guid.NewGuid()); await Seed(c, owner); var id = await Prepare(c, owner);
                var saved = await Save(c, owner, id, Guid.NewGuid(), GameReaderTests.Session()); var key = Guid.NewGuid();
                var arrived = 0; var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var writer = new Hook(new CosmosBatchWriter(c), async _ =>
                { if (Interlocked.Increment(ref arrived) == 2) ready.TrySetResult(); await ready.Task.WaitAsync(TimeSpan.FromSeconds(8)); });
                async Task<object> Run(Guid k)
                {
                    try { return await Delete(c, owner, id, k, saved.Command.ResponseEtag, GameReaderTests.Session(), writer); }
                    catch (ApiException e) { return e; }
                }
                var results = await Task.WhenAll(Run(key), Run(same ? key : Guid.NewGuid()));
                Assert.Equal(same ? 2 : 1, results.OfType<CommandResponse>().Count());
                if (same) Assert.Single(results.OfType<CommandResponse>(), r => r.Replayed);
                else Assert.Equal(410, Assert.Single(results.OfType<ApiException>()).StatusCode);
                var totals = await new LossTotalsReader(new CosmosPointReader(c)).GetAsync(owner, GameReaderTests.Session(), default);
                Assert.All(totals.Participants, p => Assert.Equal("0", p.TotalLossYen));
                Assert.Equal(6, (await c.ReadItemAsync<GameDocument>($"game:{id:D}", new PartitionKey(owner))).Resource.Revision);
            }
        }
        finally { await db.Database.DeleteAsync(); }
    }

    [EmulatorFact]
    [Trait("Category", "CosmosIntegration")]
    public async Task Each_failed_delete_batch_operation_rolls_back_game_aggregate_and_command()
    {
        using var client = CosmosIntegrationTests.Client(); var db = await client.CreateDatabaseAsync($"rollpay-g10-test-{Guid.NewGuid():N}");
        try
        {
            var c = (await db.Database.CreateContainerAsync("app", "/ownerKey")).Container;
            for (var index = 0; index < 3; index++)
            {
                var owner = OwnerKey.FromIds(Guid.NewGuid(), Guid.NewGuid()); await Seed(c, owner); var id = await Prepare(c, owner);
                var saved = await Save(c, owner, id, Guid.NewGuid(), GameReaderTests.Session()); var key = Guid.NewGuid();
                var game = await c.ReadItemAsync<GameDocument>($"game:{id:D}", new PartitionKey(owner));
                var totals = await c.ReadItemAsync<LossTotalsDocument>("lossTotals", new PartitionKey(owner));
                var writer = new Hook(new CosmosBatchWriter(c), async writes =>
                {
                    var changed = writes.ToArray();
                    if (index < 2) changed[index] = changed[index] with { Etag = "\"stale\"" };
                    else await c.CreateItemAsync((CommandDocument)changed[index].Document, new PartitionKey(owner));
                    Assert.True((await new CosmosBatchWriter(c).WriteAsync(owner, changed, null, default)).Conflict);
                    Assert.Equal(game.ETag, (await c.ReadItemAsync<GameDocument>(game.Resource.Id, new PartitionKey(owner))).ETag);
                    Assert.Equal(totals.ETag, (await c.ReadItemAsync<LossTotalsDocument>("lossTotals", new PartitionKey(owner))).ETag);
                    if (index < 2)
                    {
                        var absent = await Assert.ThrowsAsync<CosmosException>(() => c.ReadItemAsync<object>($"command:{key:D}", new PartitionKey(owner)));
                        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
                    }
                    throw new InvalidOperationException("Rollback verified.");
                });
                Assert.Equal("Rollback verified.", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    Delete(c, owner, id, key, saved.Command.ResponseEtag, GameReaderTests.Session(), writer))).Message);
            }
        }
        finally { await db.Database.DeleteAsync(); }
    }

    [EmulatorFact]
    [Trait("Category", "CosmosIntegration")]
    public async Task Different_history_delete_and_new_payment_conflicts_recompute_latest_totals()
    {
        using var client = CosmosIntegrationTests.Client(); var db = await client.CreateDatabaseAsync($"rollpay-g10-test-{Guid.NewGuid():N}");
        try
        {
            var c = (await db.Database.CreateContainerAsync("app", "/ownerKey")).Container;
            foreach (var secondDelete in new[] { true, false })
            {
                var owner = OwnerKey.FromIds(Guid.NewGuid(), Guid.NewGuid()); await Seed(c, owner);
                var first = await Prepare(c, owner); await Save(c, owner, first, Guid.NewGuid(), GameReaderTests.Session());
                var second = await Prepare(c, owner);
                if (secondDelete) await Save(c, owner, second, Guid.NewGuid(), GameReaderTests.Session());
                var arrived = 0; var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var writer = new Hook(new CosmosBatchWriter(c), async _ =>
                { if (Interlocked.Increment(ref arrived) == 2) ready.TrySetResult(); await ready.Task.WaitAsync(TimeSpan.FromSeconds(8)); });
                await Task.WhenAll(Delete(c, owner, first, Guid.NewGuid(), GameEtag.Create(first, 5), GameReaderTests.Session(), writer),
                    secondDelete ? Delete(c, owner, second, Guid.NewGuid(), GameEtag.Create(second, 5), GameReaderTests.Session(), writer)
                        : Save(c, owner, second, Guid.NewGuid(), GameReaderTests.Session(), writer));
                var totals = await new LossTotalsReader(new CosmosPointReader(c)).GetAsync(owner, GameReaderTests.Session(), default);
                Assert.Equal(secondDelete ? "0" : "1800", totals.Participants.Single(p => p.ParticipantId == "p001").TotalLossYen);
                Assert.Equal("deleted", (await c.ReadItemAsync<GameDocument>($"game:{first:D}", new PartitionKey(owner))).Resource.Status);
                Assert.Equal(secondDelete ? "deleted" : "completed", (await c.ReadItemAsync<GameDocument>($"game:{second:D}", new PartitionKey(owner))).Resource.Status);
            }
        }
        finally { await db.Database.DeleteAsync(); }
    }

    [EmulatorFact]
    [Trait("Category", "CosmosIntegration")]
    public async Task Response_loss_replays_on_fresh_client_and_old_payment_replay_does_not_restore_deleted_history()
    {
        using var client = CosmosIntegrationTests.Client(); var db = await client.CreateDatabaseAsync($"rollpay-g10-test-{Guid.NewGuid():N}");
        try
        {
            var c = (await db.Database.CreateContainerAsync(Properties())).Container;
            var owner = OwnerKey.FromIds(Guid.NewGuid(), Guid.NewGuid()); await Seed(c, owner); var id = await Prepare(c, owner);
            var saveKey = Guid.NewGuid(); var saved = await Save(c, owner, id, saveKey, GameReaderTests.Session());
            var active = await Prepare(c, owner); var control = await c.ReadItemAsync<ControlDocument>("control", new PartitionKey(owner));
            var commits = 0; var writer = new LostResponse(new CosmosBatchWriter(c), () => commits++);
            var session = GameReaderTests.Session(); var key = Guid.NewGuid();
            var deleted = await Delete(c, owner, id, key, saved.Command.ResponseEtag, session, writer);
            Assert.True(deleted.Replayed); Assert.Equal(1, commits);
            using var fresh = CosmosIntegrationTests.Client(); var other = fresh.GetContainer(db.Database.Id, "app");
            var replay = await Delete(other, owner, id, key, saved.Command.ResponseEtag, session);
            Assert.True(replay.Replayed); Assert.Equal(deleted.Command.ResponseBody.GetRawText(), replay.Command.ResponseBody.GetRawText());
            Assert.Equal(saved.Command.ResponseBody.GetRawText(), (await Save(other, owner, id, saveKey, session)).Command.ResponseBody.GetRawText());
            var reader = new HistoryReader(new CosmosHistoryStore(other), new CosmosPointReader(other));
            Assert.Empty((await reader.ListAsync(owner, new(20, null), session, default)).Items);
            Assert.Equal(410, (await Assert.ThrowsAsync<ApiException>(() => reader.DetailAsync(owner, id, session, default))).StatusCode);
            Assert.All((await new LossTotalsReader(new CosmosPointReader(other)).GetAsync(owner, session, default)).Participants, p => Assert.Equal("0", p.TotalLossYen));
            var currentControl = await other.ReadItemAsync<ControlDocument>("control", new PartitionKey(owner));
            Assert.Equal(control.ETag, currentControl.ETag); Assert.Equal(active, currentControl.Resource.ActiveGameId);
        }
        finally { await db.Database.DeleteAsync(); }
    }

    private sealed class Hook(ICosmosBatchWriter inner, Func<IReadOnlyList<BatchWrite>, Task> before) : ICosmosBatchWriter
    {
        public async Task<BatchResult> WriteAsync(string owner, IReadOnlyList<BatchWrite> writes, string? sessionToken, CancellationToken token)
        { await before(writes); return await inner.WriteAsync(owner, writes, sessionToken, token); }
    }
    private sealed class LostResponse(ICosmosBatchWriter inner, Action committed) : ICosmosBatchWriter
    {
        public async Task<BatchResult> WriteAsync(string owner, IReadOnlyList<BatchWrite> writes, string? sessionToken, CancellationToken token)
        { var result = await inner.WriteAsync(owner, writes, sessionToken, token); Assert.False(result.Conflict); committed(); throw CosmosErrors.Unavailable(); }
    }
}
