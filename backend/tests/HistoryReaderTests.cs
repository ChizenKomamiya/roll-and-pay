using Microsoft.AspNetCore.Http;
using Moq;
using RollAndPay.Api.Data;
using RollAndPay.Api.History;
using RollAndPay.Api.Http;
using Xunit;

namespace RollAndPay.Api.Tests;

public sealed class HistoryReaderTests
{
    internal static Mock<ICosmosPointReader> Points(GameDocument? game, string? etag = "internal-etag", string? session = "0:-1#2")
    {
        var points = new Mock<ICosmosPointReader>(MockBehavior.Strict);
        points.Setup(p => p.ReadAsync<GameDocument>(DataTests.Owner, $"game:{GameFixture.Id:D}", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DocumentRead<GameDocument>(game, session, etag));
        return points;
    }

    [Theory]
    [InlineData("rolling", 409, "INVALID_GAME_STATE")]
    [InlineData("tie", 409, "INVALID_GAME_STATE")]
    [InlineData("awaitingPayment", 409, "INVALID_GAME_STATE")]
    [InlineData("cancelled", 409, "INVALID_GAME_STATE")]
    [InlineData("deleted", 410, "GAME_DELETED")]
    [InlineData("missing", 404, "GAME_NOT_FOUND")]
    [InlineData("foreign", 404, "GAME_NOT_FOUND")]
    public async Task Detail_state_and_owner_boundaries(string status, int expected, string code)
    {
        var game = status == "missing" ? null : GameFixture.Game(status == "foreign" ? "deleted" : status);
        if (status == "foreign") game = game! with { OwnerKey = "other" };
        var points = Points(game);
        var history = new Mock<IHistoryStore>(MockBehavior.Strict);
        var error = await Assert.ThrowsAsync<ApiException>(() => new HistoryReader(history.Object, points.Object)
            .DetailAsync(DataTests.Owner, GameFixture.Id, ReadSession.Attach(new DefaultHttpContext()), default));
        Assert.Equal(expected, error.StatusCode); Assert.Equal(code, error.Code);
        points.VerifyAll(); history.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("payment")]
    [InlineData("participants")]
    [InlineData("duplicate")]
    [InlineData("order")]
    [InlineData("loser")]
    [InlineData("sum")]
    [InlineData("shares")]
    [InlineData("registeredAt")]
    [InlineData("completedAt")]
    [InlineData("deletedAt")]
    [InlineData("owner")]
    [InlineData("id")]
    [InlineData("status")]
    [InlineData("schema")]
    public async Task Corrupt_saved_games_fail_closed(string fault)
    {
        var game = GameFixture.Game("completed");
        game = fault switch
        {
            "payment" => game with { Payment = null },
            "participants" => game with { Participants = null! },
            "duplicate" => game with { Participants = [game.Participants[0], game.Participants[0]] },
            "order" => game with { Participants = [game.Participants[1], game.Participants[0]] },
            "loser" => game with { LoserParticipantId = "missing" },
            "sum" => game with { Payment = game.Payment! with { TotalYen = 100 } },
            "shares" => game with { Payment = game.Payment! with { Shares = [] } },
            "registeredAt" => game with { Payment = game.Payment! with { RegisteredAt = null } },
            "completedAt" => game with { CompletedAt = game.CreatedAt.AddSeconds(-1) },
            "deletedAt" => game with { DeletedAt = game.UpdatedAt },
            "owner" => game with { OwnerKey = "foreign" },
            "id" => game with { Id = "wrong" },
            "status" => game with { Status = "deleted" },
            "schema" => game with { SchemaVersion = 2 },
            _ => throw new InvalidOperationException()
        };
        var history = new Mock<IHistoryStore>();
        history.Setup(h => h.QueryAsync(DataTests.Owner, It.IsAny<HistoryQuery>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HistoryRead([game], "0:-1#3"));
        var error = await Assert.ThrowsAsync<ApiException>(() => new HistoryReader(history.Object, Points(null).Object)
            .ListAsync(DataTests.Owner, new(20, null), ReadSession.Attach(new DefaultHttpContext()), default));
        Assert.Equal(503, error.StatusCode);
        if (fault is not ("owner" or "status"))
        {
            error = await Assert.ThrowsAsync<ApiException>(() => new HistoryReader(history.Object, Points(game).Object)
                .DetailAsync(DataTests.Owner, GameFixture.Id, ReadSession.Attach(new DefaultHttpContext()), default));
            Assert.Equal(503, error.StatusCode);
        }
    }

    [Fact]
    public async Task Saved_policy_and_zero_loss_are_preserved_without_recalculation()
    {
        var game = GameFixture.Game("completed");
        game = game with { Payment = game.Payment! with { TotalYen = 200, BaseYen = 100, LossYen = 0,
            LoserPaymentYen = 100, CollectFromOthersYen = 100, CalculationVersion = "historical-policy",
            Shares = [new() { ParticipantId = "p001", AmountYen = 100 }, new() { ParticipantId = "p002", AmountYen = 100 }] } };
        var history = new Mock<IHistoryStore>(MockBehavior.Strict);
        var session = ReadSession.Attach(new DefaultHttpContext()); session.Accept("0:-1#1");
        var points = Points(game);
        var result = await new HistoryReader(history.Object, points.Object).DetailAsync(DataTests.Owner, GameFixture.Id, session, default);
        Assert.Equal(0, result.Payment.LossYen); Assert.Equal("historical-policy", result.Payment.CalculationVersion);
        Assert.Equal("当時の名前A", result.Participants[0].DisplayName);
        Assert.Equal("0:-1#2", session.Token);
        points.Verify(p => p.ReadAsync<GameDocument>(DataTests.Owner, $"game:{GameFixture.Id:D}", "0:-1#1", It.IsAny<CancellationToken>()), Times.Once);
        points.VerifyNoOtherCalls(); history.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Duplicate_unordered_or_out_of_boundary_rows_are_not_published()
    {
        var one = HistoryHttpTests.Saved(1); var two = HistoryHttpTests.Saved(2);
        foreach (var (rows, boundary) in new (GameDocument[], HistoryBoundary?)[]
        {
            ([one, one], null), ([one, two], null),
            ([two], new(two.CreatedAt, two.GameId)),
            ([one with { CreatedAt = one.CreatedAt.AddTicks(1) }], null)
        })
        {
            var history = new Mock<IHistoryStore>();
            history.Setup(h => h.QueryAsync(It.IsAny<string>(), It.IsAny<HistoryQuery>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new HistoryRead(rows, "0:-1#3"));
            var error = await Assert.ThrowsAsync<ApiException>(() => new HistoryReader(history.Object, Points(null).Object)
                .ListAsync(DataTests.Owner, new(20, boundary), ReadSession.Attach(new DefaultHttpContext()), default));
            Assert.Equal(503, error.StatusCode);
        }
    }

    [Fact]
    public async Task Caller_cancellation_is_preserved()
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var history = new Mock<IHistoryStore>();
        history.Setup(h => h.QueryAsync(It.IsAny<string>(), It.IsAny<HistoryQuery>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns<string, HistoryQuery, string?, CancellationToken>((o, q, s, t) => Task.FromCanceled<HistoryRead>(t));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new HistoryReader(history.Object, Points(null).Object)
            .ListAsync(DataTests.Owner, new(20, null), ReadSession.Attach(new DefaultHttpContext()), cancelled.Token));
    }
}
