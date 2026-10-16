using System.Net;
using Microsoft.Azure.Cosmos;
using Moq;
using RollAndPay.Api.Data;
using RollAndPay.Api.Http;
using Xunit;

namespace RollAndPay.Api.Tests;

public sealed class CosmosBatchWriterTests
{
    [Theory]
    [InlineData(200, 200, 0)]
    [InlineData(424, 409, 409)]
    [InlineData(424, 412, 409)]
    [InlineData(424, 429, 429)]
    [InlineData(424, 403, 503)]
    [InlineData(503, 424, 503)]
    public async Task Uses_owner_partition_create_or_conditional_replace_and_translates_root_failure(int overall, int root, int expected)
    {
        var container = new Mock<Container>(MockBehavior.Strict);
        var batch = new Mock<TransactionalBatch>(MockBehavior.Strict);
        var response = new Mock<TransactionalBatchResponse>();
        response.SetupGet(r => r.IsSuccessStatusCode).Returns(overall == 200);
        response.SetupGet(r => r.StatusCode).Returns((HttpStatusCode)overall);
        var headers = new Headers(); headers.Set("x-ms-session-token", "0:-1#10");
        response.SetupGet(r => r.Headers).Returns(headers);
        var dependent = new Mock<TransactionalBatchOperationResult>(); dependent.SetupGet(r => r.StatusCode).Returns((HttpStatusCode)424);
        var failure = new Mock<TransactionalBatchOperationResult>(); failure.SetupGet(r => r.StatusCode).Returns((HttpStatusCode)root);
        failure.SetupGet(r => r.RetryAfter).Returns(TimeSpan.FromMilliseconds(2100));
        response.Setup(r => r.GetEnumerator()).Returns(() => ((IEnumerable<TransactionalBatchOperationResult>)[dependent.Object, failure.Object]).GetEnumerator());
        container.Setup(c => c.CreateTransactionalBatch(new PartitionKey(DataTests.Owner))).Returns(batch.Object);
        object created = GameFixture.Game(), replaced = GameFixture.Control();
        batch.Setup(b => b.CreateItem(created, It.Is<TransactionalBatchItemRequestOptions>(o => o == null))).Returns(batch.Object);
        batch.Setup(b => b.ReplaceItem("control", replaced, It.Is<TransactionalBatchItemRequestOptions>(o => o.IfMatchEtag == "control-etag"))).Returns(batch.Object);
        batch.Setup(b => b.ExecuteAsync(It.Is<TransactionalBatchRequestOptions>(o => o.SessionToken == "0:-1#9" && o.ConsistencyLevel == ConsistencyLevel.Session), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response.Object);
        var action = () => new CosmosBatchWriter(container.Object).WriteAsync(DataTests.Owner,
            [new("game", created), new("control", replaced, "control-etag")], "0:-1#9", default);
        if (expected is 0 or 409)
        {
            var result = await action(); Assert.Equal(expected == 409, result.Conflict); Assert.Equal("0:-1#10", result.SessionToken);
        }
        else
        {
            var error = await Assert.ThrowsAsync<ApiException>(action); Assert.Equal(expected, error.StatusCode);
            if (expected == 429) Assert.Equal(3, error.RetryAfterSeconds);
        }
        batch.VerifyAll(); container.VerifyAll();
    }
}
