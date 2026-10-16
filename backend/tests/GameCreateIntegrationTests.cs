using System.Net;
using Microsoft.Azure.Cosmos;
using RollAndPay.Api.Data;
using RollAndPay.Api.Games;
using RollAndPay.Api.Http;
using RollAndPay.Seed;
using Xunit;

namespace RollAndPay.Api.Tests;

public sealed class GameCreateIntegrationTests
{
    [EmulatorFact]
    [Trait("Category", "CosmosIntegration")]
    public async Task Concurrent_creates_same_or_different_keys_commit_one_game_and_a_fresh_client_inherits_session_and_replays()
    {
        using var client = CosmosIntegrationTests.Client();
        var database = await client.CreateDatabaseAsync($"rollpay-g04-test-{Guid.NewGuid():N}");
        try
        {
            var container = (await database.Database.CreateContainerAsync("app", "/ownerKey")).Container;
            foreach (var sameKey in new[] { true, false })
            {
                var owner = OwnerKey.FromIds(Guid.NewGuid(), Guid.NewGuid());
                await new InitialDataWriter(container).CreateAsync(InitialData.Build(owner, [DataTests.Person(), DataTests.Person("p002")], DateTimeOffset.UtcNow));
                var key = Guid.NewGuid(); var secondKey = sameKey ? key : Guid.NewGuid();
                var arrived = 0; var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var coordinated = new HookWriter(new CosmosBatchWriter(container), async () =>
                {
                    if (Interlocked.Increment(ref arrived) == 2) release.TrySetResult();
                    await release.Task.WaitAsync(TimeSpan.FromSeconds(8));
                });
                var reader = new CosmosPointReader(container);
                var creator = new GameCreator(reader, coordinated, new GameReader(reader));
                async Task<object> Run(Guid operationKey)
                {
                    try { return await creator.CreateAsync(owner, operationKey, GameCreateTests.Hash(), ["p002", "p001"], GameReaderTests.Session(), default); }
                    catch (ApiException e) { return e; }
                }
                var results = await Task.WhenAll(Run(key), Run(secondKey));
                var success = results.OfType<CommandResponse>().ToArray();
                Assert.Equal(sameKey ? 2 : 1, success.Length);
                Assert.Single(success.Select(s => s.Command.GameId).Distinct());
                if (sameKey) Assert.Single(success, s => s.Replayed);
                else Assert.Equal("ACTIVE_GAME_EXISTS", Assert.Single(results.OfType<ApiException>()).Code);
                var saved = success[0];
                var committed = await container.ReadItemAsync<CommandDocument>(saved.Command.Id, new PartitionKey(owner));
                var inherited = GameReaderTests.Session(); inherited.Accept(committed.Headers.Session);
                using var fresh = CosmosIntegrationTests.Client();
                var freshReader = new CosmosPointReader(fresh.GetContainer(database.Database.Id, "app"));
                var replay = await new GameCreator(freshReader, new CosmosBatchWriter(container), new GameReader(freshReader)).CreateAsync(owner,
                    Guid.Parse(saved.Command.Id[8..]), GameCreateTests.Hash(), ["p002", "p001"], inherited, default);
                Assert.True(replay.Replayed); Assert.Equal(saved.Command.ResponseBody.GetRawText(), replay.Command.ResponseBody.GetRawText());
                var active = await new GameReader(freshReader).GetActiveAsync(owner, inherited, default);
                Assert.Equal(saved.Command.GameId, active!.GameId);
                Assert.Equal(new[] { "p002", "p001" }, active.Participants.Select(p => p.ParticipantId));
                var query = container.GetItemQueryIterator<int>(new QueryDefinition("SELECT VALUE COUNT(1) FROM c WHERE c.type = 'game'"),
                    requestOptions: new QueryRequestOptions { PartitionKey = new(owner) });
                Assert.Equal(1, (await query.ReadNextAsync()).Single());
            }
        }
        finally { await database.Database.DeleteAsync(); }
    }

    [EmulatorFact]
    [Trait("Category", "CosmosIntegration")]
    public async Task Stale_roster_ETag_rolls_back_game_round_control_and_command_and_revalidates_disabled_participant()
    {
        using var client = CosmosIntegrationTests.Client();
        var database = await client.CreateDatabaseAsync($"rollpay-g04-test-{Guid.NewGuid():N}");
        try
        {
            var container = (await database.Database.CreateContainerAsync("app", "/ownerKey")).Container;
            var data = InitialData.Build(DataTests.Owner, [DataTests.Person(), DataTests.Person("p002")], DateTimeOffset.UtcNow);
            await new InitialDataWriter(container).CreateAsync(data);
            var reader = new CosmosPointReader(container); var key = Guid.NewGuid();
            IReadOnlyList<BatchWrite>? attempted = null;
            var hooked = new HookWriter(new CosmosBatchWriter(container), async () =>
            {
                await container.ReplaceItemAsync(data.Roster with { Participants = [DataTests.Person(), DataTests.Person("p002", active: false)] }, "roster", new PartitionKey(DataTests.Owner));
            }, writes => attempted = writes);
            var error = await Assert.ThrowsAsync<ApiException>(() => new GameCreator(reader, hooked, new GameReader(reader)).CreateAsync(DataTests.Owner,
                key, GameCreateTests.Hash(), ["p002", "p001"], GameReaderTests.Session(), default));
            Assert.Equal(422, error.StatusCode);
            foreach (var write in attempted!.Where(w => w.Etag is null))
            {
                var missing = await Assert.ThrowsAsync<CosmosException>(() => container.ReadItemAsync<object>(write.Id, new PartitionKey(DataTests.Owner)));
                Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            }
            Assert.Null((await container.ReadItemAsync<ControlDocument>("control", new PartitionKey(DataTests.Owner))).Resource.ActiveGameId);
        }
        finally { await database.Database.DeleteAsync(); }
    }

    [EmulatorFact]
    [Trait("Category", "CosmosIntegration")]
    public async Task Real_commit_followed_by_transport_failure_replays_command_with_committed_session()
    {
        using var client = CosmosIntegrationTests.Client();
        var database = await client.CreateDatabaseAsync($"rollpay-g04-test-{Guid.NewGuid():N}");
        try
        {
            var container = (await database.Database.CreateContainerAsync("app", "/ownerKey")).Container;
            await new InitialDataWriter(container).CreateAsync(InitialData.Build(DataTests.Owner, [DataTests.Person(), DataTests.Person("p002")], DateTimeOffset.UtcNow));
            var reader = new CosmosPointReader(container); var session = GameReaderTests.Session();
            var lost = new LostResponseWriter(new CosmosBatchWriter(container));
            var result = await new GameCreator(reader, lost, new GameReader(reader)).CreateAsync(DataTests.Owner, Guid.NewGuid(), GameCreateTests.Hash(),
                ["p002", "p001"], session, default);
            Assert.True(result.Replayed); Assert.NotNull(session.Token); Assert.Equal(1, lost.Commits);
            using var fresh = CosmosIntegrationTests.Client();
            var game = await new GameReader(new CosmosPointReader(fresh.GetContainer(database.Database.Id, "app")))
                .GetAsync(DataTests.Owner, result.Command.GameId, session, default);
            Assert.Equal(1, game.Revision);
        }
        finally { await database.Database.DeleteAsync(); }
    }

    private sealed class HookWriter(ICosmosBatchWriter inner, Func<Task> before, Action<IReadOnlyList<BatchWrite>>? inspect = null) : ICosmosBatchWriter
    {
        public async Task<BatchResult> WriteAsync(string owner, IReadOnlyList<BatchWrite> writes, string? sessionToken, CancellationToken token)
        { inspect?.Invoke(writes); await before(); return await inner.WriteAsync(owner, writes, sessionToken, token); }
    }
    private sealed class LostResponseWriter(ICosmosBatchWriter inner) : ICosmosBatchWriter
    {
        internal int Commits;
        public async Task<BatchResult> WriteAsync(string owner, IReadOnlyList<BatchWrite> writes, string? sessionToken, CancellationToken token)
        {
            var result = await inner.WriteAsync(owner, writes, sessionToken, token);
            Assert.False(result.Conflict); Commits++; throw CosmosErrors.Unavailable();
        }
    }
}
