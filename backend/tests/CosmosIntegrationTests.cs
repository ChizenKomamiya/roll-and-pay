using System.Net;
using Microsoft.Azure.Cosmos;
using RollAndPay.Api.Data;
using RollAndPay.Api.Http;
using RollAndPay.Seed;
using Xunit;

namespace RollAndPay.Api.Tests;

public sealed class EmulatorFactAttribute : FactAttribute
{
    public EmulatorFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("ROLLPAY_RUN_COSMOS_TESTS") != "1")
            Skip = "Opt in with ROLLPAY_RUN_COSMOS_TESTS=1 and a localhost Cosmos emulator; no production DB is used.";
    }
}

public sealed class CosmosIntegrationTests
{
    internal static CosmosClient Client()
    {
        var endpoint = new Uri(Environment.GetEnvironmentVariable("ROLLPAY_EMULATOR_ENDPOINT") ?? "https://localhost:8081/");
        if (!endpoint.IsLoopback || endpoint.Scheme != "https" || endpoint.UserInfo.Length != 0 ||
            endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 || endpoint.AbsolutePath != "/")
            throw new InvalidOperationException("Integration tests require an HTTPS loopback emulator endpoint.");
        var key = Environment.GetEnvironmentVariable("ROLLPAY_EMULATOR_KEY");
        if (string.IsNullOrEmpty(key)) throw new InvalidOperationException("Set ROLLPAY_EMULATOR_KEY to the local emulator key.");
        return new CosmosClient(endpoint.ToString(), key, CosmosConnection.ClientOptions());
    }

    [EmulatorFact]
    [Trait("Category", "CosmosIntegration")]
    public async Task Initial_batch_commits_all_documents_and_a_second_client_inherits_the_session()
    {
        using var client = Client();
        var database = await client.CreateDatabaseAsync($"rollpay-g02-test-{Guid.NewGuid():N}");
        try
        {
            var container = (await database.Database.CreateContainerAsync("app", "/ownerKey")).Container;
            var data = InitialData.Build(DataTests.Owner, [DataTests.Person(), DataTests.Person("p002", active: false)], DateTimeOffset.UtcNow);
            await new InitialDataWriter(container).CreateAsync(data);
            var read = await new CosmosRosterStore(container).ReadAsync(DataTests.Owner, null, default);
            Assert.Equal(2, read.Document.Participants.Length);
            var control = await container.ReadItemAsync<ControlDocument>("control", new PartitionKey(DataTests.Owner));
            var totals = await container.ReadItemAsync<LossTotalsDocument>("lossTotals", new PartitionKey(DataTests.Owner));
            Assert.Null(control.Resource.ActiveGameId);
            Assert.All(totals.Resource.Totals, t => Assert.Equal("0", t.TotalLossYen));
            using var second = Client();
            var inherited = await new CosmosRosterStore(second.GetContainer(database.Database.Id, "app"))
                .ReadAsync(DataTests.Owner, ReadSession.Decode(DataTests.Encode(read.SessionToken)), default);
            Assert.Equal(read.Document.Participants, inherited.Document.Participants);
            var error = await Assert.ThrowsAsync<ApiException>(() => new CosmosRosterStore(container).ReadAsync("other-owner", null, default));
            Assert.Equal(503, error.StatusCode);
        }
        finally { await database.Database.DeleteAsync(); }
    }

    [EmulatorFact]
    [Trait("Category", "CosmosIntegration")]
    public async Task Any_existing_initial_document_rolls_back_the_other_creates_and_preserves_existing_data()
    {
        using var client = Client();
        var database = await client.CreateDatabaseAsync($"rollpay-g02-test-{Guid.NewGuid():N}");
        try
        {
            var container = (await database.Database.CreateContainerAsync("app", "/ownerKey")).Container;
            foreach (var existingId in new[] { "roster", "control", "lossTotals" })
            {
                var owner = OwnerKey.FromIds(Guid.NewGuid(), Guid.NewGuid());
                var data = InitialData.Build(owner, [DataTests.Person()], DateTimeOffset.UtcNow);
                var original = new Dictionary<string, object> { ["id"] = existingId, ["ownerKey"] = owner, ["marker"] = "must-be-preserved" };
                await container.CreateItemAsync(original, new PartitionKey(owner));
                var before = await container.ReadItemAsync<Dictionary<string, object>>(existingId, new PartitionKey(owner));
                await Assert.ThrowsAsync<InitialDataConflictException>(() => new InitialDataWriter(container).CreateAsync(data));
                var after = await container.ReadItemAsync<Dictionary<string, object>>(existingId, new PartitionKey(owner));
                Assert.Equal(before.ETag, after.ETag);
                Assert.Equal("must-be-preserved", after.Resource["marker"].ToString());
                foreach (var missing in new[] { "roster", "control", "lossTotals" }.Where(id => id != existingId))
                {
                    var failure = await Assert.ThrowsAsync<CosmosException>(() => container.ReadItemAsync<object>(missing, new PartitionKey(owner)));
                    Assert.Equal(HttpStatusCode.NotFound, failure.StatusCode);
                }
            }
        }
        finally { await database.Database.DeleteAsync(); }
    }
}
