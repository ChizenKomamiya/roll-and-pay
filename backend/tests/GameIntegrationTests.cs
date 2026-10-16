using Microsoft.Azure.Cosmos;
using RollAndPay.Api.Data;
using RollAndPay.Api.Games;
using RollAndPay.Api.Http;
using Xunit;

namespace RollAndPay.Api.Tests;

public sealed class GameIntegrationTests
{
    [EmulatorFact]
    [Trait("Category", "CosmosIntegration")]
    public async Task Real_Cosmos_restores_saved_states_and_respects_owner_and_deleted_boundaries()
    {
        using var client = CosmosIntegrationTests.Client();
        var database = await client.CreateDatabaseAsync($"rollpay-g03-test-{Guid.NewGuid():N}");
        try
        {
            var container = (await database.Database.CreateContainerAsync("app", "/ownerKey")).Container;
            foreach (var status in new[] { "rolling", "tie", "awaitingPayment", "completed", "cancelled" })
            {
                var owner = OwnerKey.FromIds(Guid.NewGuid(), Guid.NewGuid());
                var game = GameFixture.Game(status) with { OwnerKey = owner };
                var round = GameFixture.Round(status) with { OwnerKey = owner };
                var control = GameFixture.Control(GameReadValidation.IsActive(status) ? GameFixture.Id : null) with { OwnerKey = owner };
                using var batch = await container.CreateTransactionalBatch(new PartitionKey(owner))
                    .CreateItem(game).CreateItem(round).CreateItem(control).ExecuteAsync();
                Assert.True(batch.IsSuccessStatusCode);
                using var fresh = CosmosIntegrationTests.Client();
                var reader = new GameReader(new CosmosPointReader(fresh.GetContainer(database.Database.Id, "app")));
                var session = GameReaderTests.Session(); session.Accept(batch.Headers.Session);
                var saved = await reader.GetAsync(owner, GameFixture.Id, session, default);
                Assert.Equal(status, saved.Status);
                Assert.Equal(game.Participants, saved.Participants);
                var active = await reader.GetActiveAsync(owner, session, default);
                Assert.Equal(GameReadValidation.IsActive(status), active is not null);
            }
            var deleted = GameFixture.Game("deleted");
            await container.CreateItemAsync(deleted, new PartitionKey(DataTests.Owner));
            var deletedReader = new GameReader(new CosmosPointReader(container));
            var gone = await Assert.ThrowsAsync<ApiException>(() => deletedReader.GetAsync(DataTests.Owner, GameFixture.Id, GameReaderTests.Session(), default));
            Assert.Equal(410, gone.StatusCode);
            var foreign = await Assert.ThrowsAsync<ApiException>(() => deletedReader.GetAsync("other-owner", GameFixture.Id, GameReaderTests.Session(), default));
            Assert.Equal(404, foreign.StatusCode);
        }
        finally { await database.Database.DeleteAsync(); }
    }

    [EmulatorFact]
    [Trait("Category", "CosmosIntegration")]
    public async Task A_real_transaction_between_reads_cannot_return_a_mixed_game_and_round()
    {
        using var client = CosmosIntegrationTests.Client();
        var database = await client.CreateDatabaseAsync($"rollpay-g03-test-{Guid.NewGuid():N}");
        try
        {
            var container = (await database.Database.CreateContainerAsync("app", "/ownerKey")).Container;
            using var initial = await container.CreateTransactionalBatch(new PartitionKey(DataTests.Owner))
                .CreateItem(GameFixture.Game()).CreateItem(GameFixture.Round()).ExecuteAsync();
            Assert.True(initial.IsSuccessStatusCode);
            var changed = false;
            var hooked = new ReadHook(new CosmosPointReader(container), async () =>
            {
                if (changed) return;
                changed = true;
                var game = GameFixture.Game("awaitingPayment") with { Revision = 5 };
                var round = GameFixture.Round("awaitingPayment");
                using var updated = await container.CreateTransactionalBatch(new PartitionKey(DataTests.Owner))
                    .ReplaceItem(game.Id, game).ReplaceItem(round.Id, round).ExecuteAsync();
                Assert.True(updated.IsSuccessStatusCode);
            });
            var result = await new GameReader(hooked).GetAsync(DataTests.Owner, GameFixture.Id, GameReaderTests.Session(), default);
            Assert.True(changed);
            Assert.Equal(5, result.Revision);
            Assert.Equal("awaitingPayment", result.Status);
            Assert.Equal("decided", result.CurrentRound.Status);
            Assert.Equal("p001", result.LoserParticipantId);
            Assert.Equal(2, hooked.RoundReads);
        }
        finally { await database.Database.DeleteAsync(); }
    }

    private sealed class ReadHook(ICosmosPointReader inner, Func<Task> afterRound) : ICosmosPointReader
    {
        public int RoundReads { get; private set; }
        public async Task<DocumentRead<T>> ReadAsync<T>(string ownerKey, string id, string? sessionToken, CancellationToken cancellationToken) where T : class
        {
            var read = await inner.ReadAsync<T>(ownerKey, id, sessionToken, cancellationToken);
            if (typeof(T) == typeof(RoundDocument)) { RoundReads++; await afterRound(); }
            return read;
        }
    }
}
