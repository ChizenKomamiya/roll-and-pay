using System.Net;
using Microsoft.Azure.Cosmos;
using Moq;
using RollAndPay.Api.Data;
using RollAndPay.Api.History;
using RollAndPay.Api.Http;
using Xunit;

namespace RollAndPay.Api.Tests;

public sealed class CosmosHistoryReadTests
{
    private static FeedResponse<GameDocument> Page(GameDocument[] games, string? session)
    {
        var response = new Mock<FeedResponse<GameDocument>>();
        var headers = new Headers();
        if (session is not null) headers.Set("x-ms-session-token", session);
        response.SetupGet(r => r.Headers).Returns(headers);
        response.Setup(r => r.GetEnumerator()).Returns(() => ((IEnumerable<GameDocument>)games).GetEnumerator());
        return response.Object;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scoped_parameterized_query_drains_short_and_empty_SDK_pages(bool continuation)
    {
        var container = new Mock<Container>(MockBehavior.Strict);
        var pages = new[] { Page([], "0:-1#2"), Page([HistoryHttpTests.Saved(3)], "0:-1#3"),
            Page([HistoryHttpTests.Saved(2), HistoryHttpTests.Saved(1)], "0:-1#4") };
        var index = 0;
        QueryDefinition? sql = null; QueryRequestOptions? options = null;
        var iterator = new Mock<FeedIterator<GameDocument>>();
        iterator.SetupGet(i => i.HasMoreResults).Returns(() => index < pages.Length);
        iterator.Setup(i => i.ReadNextAsync(It.IsAny<CancellationToken>())).Returns(() =>
        {
            Assert.Equal(new[] { "0:-1#1", "0:-1#2", "0:-1#3" }[index], options!.SessionToken);
            return Task.FromResult(pages[index++]);
        });
        container.Setup(c => c.GetItemQueryIterator<GameDocument>(It.IsAny<QueryDefinition>(), null, It.IsAny<QueryRequestOptions>()))
            .Callback<QueryDefinition, string?, QueryRequestOptions>((q, c, o) => { sql = q; options = o; })
            .Returns(iterator.Object);
        var boundary = continuation ? new HistoryBoundary(GameFixture.Now.AddMilliseconds(123), GameFixture.Id) : null;
        var read = await new CosmosHistoryStore(container.Object).QueryAsync(DataTests.Owner, new(2, boundary), "0:-1#1", default);
        Assert.Equal(3, read.Games.Count); Assert.Equal("0:-1#4", read.SessionToken);
        Assert.Equal(new PartitionKey(DataTests.Owner), options!.PartitionKey);
        Assert.Equal(ConsistencyLevel.Session, options.ConsistencyLevel); Assert.Equal(3, options.MaxItemCount);
        var parameters = sql!.GetQueryParameters().ToDictionary(p => p.Name, p => p.Value);
        Assert.Equal(3, parameters["@take"]);
        Assert.Contains("c.type = 'game' AND c.status = 'completed'", sql.QueryText);
        Assert.EndsWith("ORDER BY c.type ASC, c.status ASC, c.createdAt DESC, c.gameId DESC", sql.QueryText);
        Assert.DoesNotContain(DataTests.Owner, sql.QueryText);
        Assert.DoesNotContain("round:", sql.QueryText);
        if (continuation)
        {
            Assert.Equal("2026-10-16T00:00:00.123Z", parameters["@lastDate"]);
            Assert.Equal(GameFixture.Id.ToString("D"), parameters["@lastId"]);
            Assert.Contains("c.createdAt < @lastDate OR (c.createdAt = @lastDate AND c.gameId < @lastId)", sql.QueryText);
        }
        else Assert.Single(parameters);
        iterator.Verify(i => i.ReadNextAsync(It.IsAny<CancellationToken>()), Times.Exactly(3));
        container.VerifyAll();
    }

    [Theory]
    [InlineData(400, 0, true, 400, "INVALID_READ_SESSION")]
    [InlineData(400, 0, false, 503, "TEMPORARILY_UNAVAILABLE")]
    [InlineData(404, 0, false, 503, "TEMPORARILY_UNAVAILABLE")]
    [InlineData(404, 1002, true, 503, "TEMPORARILY_UNAVAILABLE")]
    [InlineData(403, 0, true, 503, "TEMPORARILY_UNAVAILABLE")]
    [InlineData(429, 0, true, 429, "RATE_LIMITED")]
    public async Task Cosmos_errors_remain_errors_instead_of_empty_history(int status, int substatus, bool session, int expected, string code)
    {
        var iterator = new Mock<FeedIterator<GameDocument>>();
        iterator.SetupGet(i => i.HasMoreResults).Returns(true);
        iterator.Setup(i => i.ReadNextAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new CosmosException("private diagnostic", (HttpStatusCode)status, substatus, "activity", 1));
        var container = new Mock<Container>();
        container.Setup(c => c.GetItemQueryIterator<GameDocument>(It.IsAny<QueryDefinition>(), null, It.IsAny<QueryRequestOptions>())).Returns(iterator.Object);
        var error = await Assert.ThrowsAsync<ApiException>(() => new CosmosHistoryStore(container.Object)
            .QueryAsync(DataTests.Owner, new(20, null), session ? "0:-1#1" : null, default));
        Assert.Equal(expected, error.StatusCode); Assert.Equal(code, error.Code);
        Assert.DoesNotContain("private", error.Message);
        iterator.Verify(i => i.ReadNextAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Empty_page_must_also_have_a_session()
    {
        var iterator = new Mock<FeedIterator<GameDocument>>();
        iterator.SetupGet(i => i.HasMoreResults).Returns(true);
        iterator.Setup(i => i.ReadNextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Page([], null));
        var container = new Mock<Container>();
        container.Setup(c => c.GetItemQueryIterator<GameDocument>(It.IsAny<QueryDefinition>(), null, It.IsAny<QueryRequestOptions>())).Returns(iterator.Object);
        var error = await Assert.ThrowsAsync<ApiException>(() => new CosmosHistoryStore(container.Object)
            .QueryAsync(DataTests.Owner, new(20, null), null, default));
        Assert.Equal(503, error.StatusCode);
    }
}
