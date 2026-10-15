using System.Net;
using System.Text.Json;
using Microsoft.Azure.Cosmos;
using Moq;
using RollAndPay.Api.Data;
using RollAndPay.Seed;
using Xunit;

namespace RollAndPay.Api.Tests;

public sealed class InitialDataTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-16T00:00:00Z");

    [Fact]
    public void Builds_three_documents_for_one_owner_with_empty_control_and_zero_totals()
    {
        var data = InitialData.Build(DataTests.Owner, [DataTests.Person(), DataTests.Person("p002", active: false)], Now);
        Assert.Equal("roster", data.Roster.Id);
        Assert.Equal("control", data.Control.Id);
        Assert.Equal("lossTotals", data.LossTotals.Id);
        Assert.Null(data.Control.ActiveGameId);
        Assert.Equal(DataTests.Owner, data.Control.OwnerKey);
        Assert.Equal(DataTests.Owner, data.LossTotals.OwnerKey);
        Assert.Equal(data.Roster.UpdatedAt, data.LossTotals.UpdatedAt);
        Assert.Equal(new[] { "p001", "p002" }, data.LossTotals.Totals.Select(t => t.ParticipantId));
        Assert.All(data.LossTotals.Totals, t => Assert.Equal("0", t.TotalLossYen));
        var json = JsonSerializer.Serialize(data.Roster, CosmosConnection.ClientOptions().UseSystemTextJsonSerializerWithOptions);
        Assert.Contains("\"updatedAt\":\"2026-10-16T00:00:00.000Z\"", json);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Writer_uses_one_transaction_with_exactly_three_Create_operations(bool conflict)
    {
        var container = new Mock<Container>(MockBehavior.Strict);
        var batch = new Mock<TransactionalBatch>(MockBehavior.Strict);
        var result = new Mock<TransactionalBatchResponse>();
        result.SetupGet(r => r.IsSuccessStatusCode).Returns(!conflict);
        result.SetupGet(r => r.StatusCode).Returns(conflict ? HttpStatusCode.Conflict : HttpStatusCode.OK);
        container.Setup(c => c.CreateTransactionalBatch(new PartitionKey(DataTests.Owner))).Returns(batch.Object);
        batch.Setup(b => b.CreateItem(It.Is<RosterDocument>(d => d.Id == "roster" && d.OwnerKey == DataTests.Owner), It.IsAny<TransactionalBatchItemRequestOptions>())).Returns(batch.Object);
        batch.Setup(b => b.CreateItem(It.Is<ControlDocument>(d => d.Id == "control" && d.ActiveGameId == null && d.OwnerKey == DataTests.Owner), It.IsAny<TransactionalBatchItemRequestOptions>())).Returns(batch.Object);
        batch.Setup(b => b.CreateItem(It.Is<LossTotalsDocument>(d => d.Id == "lossTotals" && d.OwnerKey == DataTests.Owner && d.Totals.All(t => t.TotalLossYen == "0")), It.IsAny<TransactionalBatchItemRequestOptions>())).Returns(batch.Object);
        batch.Setup(b => b.ExecuteAsync(It.IsAny<CancellationToken>())).ReturnsAsync(result.Object);
        var writer = new InitialDataWriter(container.Object);
        var data = InitialData.Build(DataTests.Owner, [DataTests.Person()], Now);
        if (conflict) await Assert.ThrowsAsync<InitialDataConflictException>(() => writer.CreateAsync(data));
        else await writer.CreateAsync(data);
        batch.VerifyAll(); container.VerifyAll();
        batch.Verify(b => b.ExecuteAsync(It.IsAny<CancellationToken>()), Times.Once);
        // Strict mocks reject Upsert/Replace or separate writes; actual atomic rollback is covered by emulator tests.
    }

    private static string[] Args(string file) => ["--endpoint", "https://example.documents.azure.com/", "--database", "rollpay", "--container", "app",
        "--tenant-id", DataTests.Owner.Split(':')[0], "--administrator-object-id", DataTests.Owner.Split(':')[1], "--participants-file", file];

    [Fact]
    public void Default_is_local_validation_and_apply_requires_exact_owner_confirmation()
    {
        Assert.False(SeedOptions.Parse(Args("participants.local.json")).Apply);
        Assert.Throws<ArgumentException>(() => SeedOptions.Parse([.. Args("input"), "--apply"]));
        Assert.Throws<ArgumentException>(() => SeedOptions.Parse([.. Args("input"), "--apply", "--confirm-owner", "other"]));
        Assert.True(SeedOptions.Parse([.. Args("input"), "--apply", "--confirm-owner", DataTests.Owner]).Apply);
        Assert.Throws<ArgumentException>(() => SeedOptions.Parse([.. Args("input"), "--unknown", "value"]));
        Assert.Throws<ArgumentException>(() => SeedOptions.Parse([.. Args("input"), "--database", "other"]));
        Assert.Throws<ArgumentException>(() => SeedOptions.Parse([.. Args("input"), "--apply", "--apply"]));
    }

    [Theory]
    [InlineData("{\"participants\":[]}", true)]
    [InlineData("{\"participants\":[{\"participantId\":\"p001\",\"displayName\":\"A\",\"sortOrder\":0,\"isActive\":true}]}", true)]
    [InlineData("{\"participants\":[{\"participantId\":\"p001\",\"displayName\":\"A\",\"sortOrder\":0}]}", false)]
    [InlineData("{\"participants\":[],\"ownerKey\":\"other\"}", false)]
    [InlineData("{\"participants\":[],\"participants\":[]}", false)]
    [InlineData("{\"participants\":null}", false)]
    public async Task Participant_input_is_validated_before_any_connection(string json, bool valid)
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, json);
            var options = SeedOptions.Parse(Args(path));
            if (valid) Assert.NotNull(await options.ReadAsync());
            else await Assert.ThrowsAnyAsync<Exception>(() => options.ReadAsync());
        }
        finally { File.Delete(path); }
    }
}
