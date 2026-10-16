using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Azure.Cosmos;
using Moq;
using RollAndPay.Api.Data;
using RollAndPay.Api.Http;
using Xunit;

namespace RollAndPay.Api.Tests;

public sealed class CosmosGameReadTests
{
    [Fact]
    public async Task Uses_exact_id_owner_partition_and_session_consistency()
    {
        var container = new Mock<Container>(MockBehavior.Strict);
        var response = new Mock<ItemResponse<GameDocument>>();
        var headers = new Headers(); headers.Set("x-ms-session-token", "0:-1#2");
        response.SetupGet(r => r.Headers).Returns(headers);
        response.SetupGet(r => r.ETag).Returns("internal-etag");
        response.SetupGet(r => r.Resource).Returns(GameFixture.Game());
        container.Setup(c => c.ReadItemAsync<GameDocument>($"game:{GameFixture.Id:D}", new PartitionKey(DataTests.Owner),
            It.Is<ItemRequestOptions>(o => o.SessionToken == "0:-1#1" && o.ConsistencyLevel == ConsistencyLevel.Session), It.IsAny<CancellationToken>())).ReturnsAsync(response.Object);
        var read = await new CosmosPointReader(container.Object).ReadAsync<GameDocument>(DataTests.Owner, $"game:{GameFixture.Id:D}", "0:-1#1", default);
        Assert.Equal("0:-1#2", read.SessionToken); Assert.Equal("internal-etag", read.Etag);
        container.VerifyAll();
    }

    [Theory]
    [InlineData(404, 0, 0)]
    [InlineData(404, 1002, 503)]
    [InlineData(429, 0, 429)]
    [InlineData(400, 0, 400)]
    [InlineData(403, 0, 503)]
    public async Task Does_not_treat_session_not_available_as_a_missing_game(int status, int substatus, int expected)
    {
        var container = new Mock<Container>(MockBehavior.Strict);
        container.Setup(c => c.ReadItemAsync<GameDocument>(It.IsAny<string>(), new PartitionKey(DataTests.Owner), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new CosmosException("sensitive-diagnostics", (HttpStatusCode)status, substatus, "activity", 1));
        if (expected == 0) container.Setup(c => c.ReadContainerAsync(It.IsAny<ContainerRequestOptions>(), It.IsAny<CancellationToken>())).ReturnsAsync(Mock.Of<ContainerResponse>());
        var reader = new CosmosPointReader(container.Object);
        if (expected == 0) Assert.Null((await reader.ReadAsync<GameDocument>(DataTests.Owner, "game", "0:-1#1", default)).Document);
        else
        {
            var error = await Assert.ThrowsAsync<ApiException>(() => reader.ReadAsync<GameDocument>(DataTests.Owner, "game", "0:-1#1", default));
            Assert.Equal(expected, error.StatusCode);
            Assert.DoesNotContain("sensitive", error.Message);
        }
        container.VerifyAll();
    }

    [Fact]
    public async Task Missing_container_is_503_rather_than_a_game_404()
    {
        var container = new Mock<Container>(MockBehavior.Strict);
        var missing = new CosmosException("missing-resource", HttpStatusCode.NotFound, 0, "activity", 1);
        container.Setup(c => c.ReadItemAsync<GameDocument>(It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>())).ThrowsAsync(missing);
        container.Setup(c => c.ReadContainerAsync(It.IsAny<ContainerRequestOptions>(), It.IsAny<CancellationToken>())).ThrowsAsync(missing);
        var error = await Assert.ThrowsAsync<ApiException>(() => new CosmosPointReader(container.Object).ReadAsync<GameDocument>(DataTests.Owner, "game", null, default));
        Assert.Equal(503, error.StatusCode);
    }

    [Fact]
    public void Schema_version_one_without_deletedAt_remains_readable_and_missing_required_fields_do_not()
    {
        var options = CosmosConnection.ClientOptions().UseSystemTextJsonSerializerWithOptions;
        var json = JsonSerializer.SerializeToNode(GameFixture.Game("completed"), options)!;
        json.AsObject().Remove("deletedAt");
        var game = json.Deserialize<GameDocument>(options)!;
        Assert.Null(game.DeletedAt);
        Assert.Equal(JsonSerializer.Serialize(GameFixture.Game("completed").Payment, options), JsonSerializer.Serialize(game.Payment, options));
        json.AsObject().Remove("currentRoundNumber");
        Assert.Throws<JsonException>(() => json.Deserialize<GameDocument>(options));
    }
}
