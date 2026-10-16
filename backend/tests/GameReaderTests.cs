using Microsoft.AspNetCore.Http;
using Moq;
using RollAndPay.Api.Data;
using RollAndPay.Api.Games;
using RollAndPay.Api.Http;
using Xunit;

namespace RollAndPay.Api.Tests;

public sealed class GameReaderTests
{
    internal static ReadSession Session() => ReadSession.Attach(new DefaultHttpContext());
    internal static Mock<ICosmosPointReader> Store(GameDocument? game = null, RoundDocument? round = null, ControlDocument? control = null)
    {
        var store = new Mock<ICosmosPointReader>(MockBehavior.Strict);
        store.Setup(s => s.ReadAsync<GameDocument>(DataTests.Owner, $"game:{GameFixture.Id:D}", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DocumentRead<GameDocument>(game, "0:-1#2", "internal-game-etag"));
        store.Setup(s => s.ReadAsync<RoundDocument>(DataTests.Owner, $"round:{GameFixture.Id:D}:1", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DocumentRead<RoundDocument>(round, "0:-1#3", "internal-round-etag"));
        store.Setup(s => s.ReadAsync<ControlDocument>(DataTests.Owner, "control", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DocumentRead<ControlDocument>(control, "0:-1#4", "internal-control-etag"));
        return store;
    }

    [Theory]
    [InlineData("rolling")]
    [InlineData("tie")]
    [InlineData("awaitingPayment")]
    [InlineData("completed")]
    [InlineData("cancelled")]
    public async Task Restores_each_saved_state_with_current_round_and_original_names(string status)
    {
        var game = GameFixture.Game(status);
        var round = GameFixture.Round(status);
        var store = Store(game, round);
        var result = await new GameReader(store.Object).GetAsync(DataTests.Owner, GameFixture.Id, Session(), default);
        Assert.Equal(status, result.Status);
        Assert.Equal(game.Participants, result.Participants);
        Assert.Equal(round.Rolls, result.CurrentRound.Rolls);
        Assert.Equal(game.Payment, result.Payment);
        Assert.Equal($"\"g-{GameFixture.Id:D}-v4\"", GameEtag.Create(result.GameId, result.Revision));
        store.Verify(s => s.ReadAsync<GameDocument>(DataTests.Owner, game.Id, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Theory]
    [InlineData("rolling")]
    [InlineData("tie")]
    [InlineData("awaitingPayment")]
    public async Task Active_restores_only_unfinished_states_and_reconfirms_control(string status)
    {
        var store = Store(GameFixture.Game(status), GameFixture.Round(status), GameFixture.Control(GameFixture.Id));
        var result = await new GameReader(store.Object).GetActiveAsync(DataTests.Owner, Session(), default);
        Assert.Equal(status, result!.Status);
        store.Verify(s => s.ReadAsync<ControlDocument>(DataTests.Owner, "control", It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task No_active_game_reconfirms_null_pointer_without_reading_a_game()
    {
        var store = Store(control: GameFixture.Control());
        Assert.Null(await new GameReader(store.Object).GetActiveAsync(DataTests.Owner, Session(), default));
        store.Verify(s => s.ReadAsync<ControlDocument>(DataTests.Owner, "control", It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        Assert.Equal(2, store.Invocations.Count);
    }

    [Theory]
    [InlineData("missing", 404, "GAME_NOT_FOUND")]
    [InlineData("owner", 404, "GAME_NOT_FOUND")]
    [InlineData("deleted", 410, "GAME_DELETED")]
    public async Task Detail_does_not_read_round_or_disclose_other_owner_or_deleted_data(string fault, int status, string code)
    {
        var game = fault == "missing" ? null : GameFixture.Game(fault == "deleted" ? "deleted" : "rolling");
        if (fault == "owner") game = game! with { OwnerKey = "other-owner" };
        var store = Store(game);
        var error = await Assert.ThrowsAsync<ApiException>(() => new GameReader(store.Object).GetAsync(DataTests.Owner, GameFixture.Id, Session(), default));
        Assert.Equal(status, error.StatusCode); Assert.Equal(code, error.Code);
        Assert.Single(store.Invocations);
    }

    [Theory]
    [InlineData("missing-control")]
    [InlineData("wrong-control-owner")]
    [InlineData("empty-guid")]
    [InlineData("missing-game")]
    [InlineData("completed")]
    [InlineData("cancelled")]
    [InlineData("deleted")]
    public async Task Invalid_active_pointer_is_503_without_any_repair(string fault)
    {
        var control = GameFixture.Control(GameFixture.Id);
        if (fault == "missing-control") control = null;
        if (fault == "wrong-control-owner") control = control! with { OwnerKey = "other" };
        if (fault == "empty-guid") control = control! with { ActiveGameId = Guid.Empty };
        var store = Store(fault == "missing-game" ? null : GameFixture.Game(fault is "completed" or "cancelled" or "deleted" ? fault : "rolling"),
            GameFixture.Round(fault), control);
        var error = await Assert.ThrowsAsync<ApiException>(() => new GameReader(store.Object).GetActiveAsync(DataTests.Owner, Session(), default));
        Assert.Equal(503, error.StatusCode);
        Assert.Equal(5, error.RetryAfterSeconds);
    }

    [Fact]
    public async Task Game_change_during_round_read_retries_before_validating_the_mixed_snapshot()
    {
        var first = GameFixture.Game();
        var last = GameFixture.Game("awaitingPayment") with { Revision = 5 };
        var round = GameFixture.Round("awaitingPayment");
        var store = Store(first, round);
        store.SetupSequence(s => s.ReadAsync<GameDocument>(DataTests.Owner, first.Id, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DocumentRead<GameDocument>(first, "0:-1#2", "old"))
            .ReturnsAsync(new DocumentRead<GameDocument>(last, "0:-1#4", "new"))
            .ReturnsAsync(new DocumentRead<GameDocument>(last, "0:-1#5", "new"))
            .ReturnsAsync(new DocumentRead<GameDocument>(last, "0:-1#6", "new"));
        var result = await new GameReader(store.Object).GetAsync(DataTests.Owner, GameFixture.Id, Session(), default);
        Assert.Equal(5, result.Revision); Assert.Equal("awaitingPayment", result.Status);
        Assert.Equal(6, store.Invocations.Count);
    }

    [Fact]
    public async Task Active_pointer_change_to_null_retries_and_does_not_return_a_finished_game()
    {
        var store = Store(GameFixture.Game("completed"), GameFixture.Round("completed"));
        store.SetupSequence(s => s.ReadAsync<ControlDocument>(DataTests.Owner, "control", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DocumentRead<ControlDocument>(GameFixture.Control(GameFixture.Id), "0:-1#1", "old"))
            .ReturnsAsync(new DocumentRead<ControlDocument>(GameFixture.Control(), "0:-1#5", "new"))
            .ReturnsAsync(new DocumentRead<ControlDocument>(GameFixture.Control(), "0:-1#6", "new"))
            .ReturnsAsync(new DocumentRead<ControlDocument>(GameFixture.Control(), "0:-1#7", "new"));
        Assert.Null(await new GameReader(store.Object).GetActiveAsync(DataTests.Owner, Session(), default));
        Assert.Equal(7, store.Invocations.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Continuous_game_or_control_changes_stop_after_three_attempts(bool active)
    {
        var store = Store(GameFixture.Game(), GameFixture.Round(), GameFixture.Control(GameFixture.Id));
        var sequence = 0;
        if (active)
            store.Setup(s => s.ReadAsync<ControlDocument>(DataTests.Owner, "control", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new DocumentRead<ControlDocument>(GameFixture.Control(GameFixture.Id), "0:-1#5", $"control-{sequence++}"));
        else
            store.Setup(s => s.ReadAsync<GameDocument>(DataTests.Owner, $"game:{GameFixture.Id:D}", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new DocumentRead<GameDocument>(GameFixture.Game(), "0:-1#2", $"game-{sequence++}"));
        var reader = new GameReader(store.Object);
        var error = await Assert.ThrowsAsync<ApiException>(async () =>
        {
            if (active) await reader.GetActiveAsync(DataTests.Owner, Session(), default);
            else await reader.GetAsync(DataTests.Owner, GameFixture.Id, Session(), default);
        });
        Assert.Equal(503, error.StatusCode);
        Assert.Equal(GameReader.MaxAttempts * (active ? 5 : 3), store.Invocations.Count);
    }

    [Theory]
    [InlineData("missing-round")]
    [InlineData("round-owner")]
    [InlineData("round-game")]
    [InlineData("round-number")]
    [InlineData("roll-value")]
    [InlineData("roll-time")]
    [InlineData("roll-order")]
    [InlineData("schema")]
    [InlineData("participant")]
    [InlineData("loser")]
    [InlineData("payment")]
    [InlineData("completion")]
    public async Task Stable_but_inconsistent_saved_state_is_503(string fault)
    {
        var game = GameFixture.Game("completed");
        var round = GameFixture.Round("completed");
        switch (fault)
        {
            case "missing-round": round = null; break;
            case "round-owner": round = round with { OwnerKey = "other" }; break;
            case "round-game": round = round with { GameId = Guid.NewGuid() }; break;
            case "round-number": round = round with { RoundNumber = 2 }; break;
            case "roll-value": round = round with { Rolls = [round.Rolls[0] with { Value = 7 }, round.Rolls[1]] }; break;
            case "roll-time": round = round with { Rolls = [round.Rolls[0] with { RolledAt = null }, round.Rolls[1]] }; break;
            case "roll-order": round = round with { Rolls = round.Rolls.Reverse().ToArray() }; break;
            case "schema": game = game with { SchemaVersion = 2 }; break;
            case "participant": game = game with { Participants = [game.Participants[0], game.Participants[0]] }; break;
            case "loser": game = game with { LoserParticipantId = "p002" }; break;
            case "payment": game = game with { Payment = game.Payment! with { TotalYen = 10 } }; break;
            case "completion": game = game with { CompletedAt = null }; break;
        }
        var error = await Assert.ThrowsAsync<ApiException>(() => new GameReader(Store(game, round).Object).GetAsync(DataTests.Owner, GameFixture.Id, Session(), default));
        Assert.Equal(503, error.StatusCode);
    }

    [Fact]
    public async Task Session_from_each_read_is_forwarded_to_the_next_read_including_reconfirmation()
    {
        var store = new Mock<ICosmosPointReader>(MockBehavior.Strict);
        store.Setup(s => s.ReadAsync<GameDocument>(DataTests.Owner, $"game:{GameFixture.Id:D}", "0:-1#1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DocumentRead<GameDocument>(GameFixture.Game(), "0:-1#2", "same"));
        store.Setup(s => s.ReadAsync<RoundDocument>(DataTests.Owner, $"round:{GameFixture.Id:D}:1", "0:-1#2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DocumentRead<RoundDocument>(GameFixture.Round(), "0:-1#3", "round"));
        store.Setup(s => s.ReadAsync<GameDocument>(DataTests.Owner, $"game:{GameFixture.Id:D}", "0:-1#3", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DocumentRead<GameDocument>(GameFixture.Game(), "0:-1#4", "same"));
        var session = Session(); session.Accept("0:-1#1");
        await new GameReader(store.Object).GetAsync(DataTests.Owner, GameFixture.Id, session, default);
        Assert.Equal("0:-1#4", session.Token);
        store.VerifyAll();
    }

    [Theory]
    [InlineData("tie")]
    [InlineData("awaitingPayment")]
    public async Task Cancelled_games_preserve_the_previously_decided_or_tied_round(string previous)
    {
        var game = GameFixture.Game(previous) with { Status = "cancelled", CancelledAt = GameFixture.Now.AddMinutes(2) };
        var result = await new GameReader(Store(game, GameFixture.Round(previous)).Object).GetAsync(DataTests.Owner, GameFixture.Id, Session(), default);
        Assert.Equal(game.LoserParticipantId, result.LoserParticipantId);
        Assert.Equal(previous == "tie" ? "tied" : "decided", result.CurrentRound.Status);
    }

    [Fact]
    public async Task Saved_zero_loss_payment_is_restored_without_recalculating_it()
    {
        var game = GameFixture.Game("completed");
        game = game with { Payment = game.Payment! with { TotalYen = 2000, LossYen = 0, LoserPaymentYen = 1000,
            Shares = [new() { ParticipantId = "p001", AmountYen = 1000 }, new() { ParticipantId = "p002", AmountYen = 1000 }] } };
        var result = await new GameReader(Store(game, GameFixture.Round("completed")).Object).GetAsync(DataTests.Owner, GameFixture.Id, Session(), default);
        Assert.Equal(0, result.Payment!.LossYen);
    }

    [Fact]
    public async Task Caller_cancellation_is_preserved_and_internal_timeout_is_503()
    {
        var store = Store();
        store.Setup(s => s.ReadAsync<GameDocument>(DataTests.Owner, $"game:{GameFixture.Id:D}", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        var reader = new GameReader(store.Object);
        var error = await Assert.ThrowsAsync<ApiException>(() => reader.GetAsync(DataTests.Owner, GameFixture.Id, Session(), default));
        Assert.Equal(503, error.StatusCode);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.GetAsync(DataTests.Owner, GameFixture.Id, Session(), cancelled.Token));
    }
}
