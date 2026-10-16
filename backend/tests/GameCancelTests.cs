using System.Text.Json;
using Moq;
using RollAndPay.Api.Data;
using RollAndPay.Api.Games;
using RollAndPay.Api.Http;
using Xunit;

namespace RollAndPay.Api.Tests;

public sealed class GameCancelTests
{
    internal static string Etag => GameEtag.Create(GameFixture.Id, 4);
    internal static string Hash(string? etag = null, Guid? id = null)
    {
        using var json = JsonDocument.Parse("{}");
        return UpdateRequest.Hash("POST", $"/api/v1/games/{id ?? GameFixture.Id:D}/cancel", json.RootElement, etag ?? Etag);
    }
    internal static CreateStore Store(string status = "rolling")
    {
        var store = new CreateStore();
        store.Put(GameFixture.Game(status), $"game:{GameFixture.Id:D}");
        store.Put(GameFixture.Round(status), $"round:{GameFixture.Id:D}:1");
        store.Put(GameFixture.Control(GameFixture.Id), "control");
        return store;
    }
    internal static Task<CommandResponse> Cancel(CreateStore store, Guid? key = null, string? etag = null, Guid? id = null) =>
        new GameCanceller(store, store).CancelAsync(DataTests.Owner, id ?? GameFixture.Id, key ?? Guid.NewGuid(),
            Hash(etag, id), etag ?? Etag, GameReaderTests.Session(), default);

    [Theory]
    [InlineData("rolling")]
    [InlineData("tie")]
    [InlineData("awaitingPayment")]
    public async Task Cancels_only_game_control_and_command_preserving_all_rolls_and_loser(string status)
    {
        var store = Store(status);
        var original = GameFixture.Game(status); var round = store.Documents[$"round:{GameFixture.Id:D}:1"];
        var roster = store.Documents["roster"];
        var result = await Cancel(store);
        Assert.False(result.Replayed); Assert.Equal(200, result.Command.ResponseStatus); Assert.Null(result.Command.ResponseLocation);
        Assert.Equal(GameEtag.Create(GameFixture.Id, 5), result.Command.ResponseEtag);
        Assert.Equal(3, store.LastBatch!.Count);
        Assert.Equal(new[] { true, true, false }, store.LastBatch.Select(w => w.Etag is not null));
        var cancelled = (GameDocument)store.Documents[original.Id].Document;
        Assert.Equal("cancelled", cancelled.Status); Assert.Equal(5, cancelled.Revision);
        Assert.Equal(cancelled.UpdatedAt, cancelled.CancelledAt); Assert.True(cancelled.CancelledAt >= original.UpdatedAt);
        Assert.Null(cancelled.Payment); Assert.Null(cancelled.CompletedAt);
        Assert.Equal(original.LoserParticipantId, cancelled.LoserParticipantId); Assert.Equal(original.Participants, cancelled.Participants);
        Assert.Equal(original.CurrentRoundNumber, cancelled.CurrentRoundNumber); Assert.Equal(original.CreatedAt, cancelled.CreatedAt);
        Assert.Equal(round, store.Documents[$"round:{GameFixture.Id:D}:1"]); Assert.Equal(roster, store.Documents["roster"]);
        Assert.Null(((ControlDocument)store.Documents["control"].Document).ActiveGameId);
        Assert.Equal(cancelled.CancelledAt, ((ControlDocument)store.Documents["control"].Document).UpdatedAt);
        var reader = new GameReader(store);
        Assert.Null(await reader.GetActiveAsync(DataTests.Owner, GameReaderTests.Session(), default));
        var detail = await reader.GetAsync(DataTests.Owner, GameFixture.Id, GameReaderTests.Session(), default);
        Assert.Equal("cancelled", detail.Status); Assert.Equal(GameFixture.Round(status).Rolls, detail.CurrentRound.Rolls);
    }

    [Theory]
    [InlineData("completed", false, 409, "INVALID_GAME_STATE")]
    [InlineData("cancelled", false, 409, "INVALID_GAME_STATE")]
    [InlineData("completed", true, 412, "VERSION_MISMATCH")]
    [InlineData("cancelled", true, 412, "VERSION_MISMATCH")]
    [InlineData("deleted", true, 410, "GAME_DELETED")]
    [InlineData("deleted", false, 410, "GAME_DELETED")]
    [InlineData("rolling", true, 412, "VERSION_MISMATCH")]
    public async Task Validates_deleted_then_revision_then_state_without_writes(string state, bool stale, int status, string code)
    {
        var store = Store(state);
        var error = await Assert.ThrowsAsync<ApiException>(() => Cancel(store, etag: stale ? GameEtag.Create(GameFixture.Id, 3) : Etag));
        Assert.Equal(status, error.StatusCode); Assert.Equal(code, error.Code); Assert.Equal(0, store.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_and_foreign_game_are_404_before_version_checks(bool foreign)
    {
        var store = Store(); var id = $"game:{GameFixture.Id:D}";
        if (foreign) store.Put(GameFixture.Game() with { OwnerKey = "other" }, id); else store.Documents.Remove(id);
        var error = await Assert.ThrowsAsync<ApiException>(() => Cancel(store, etag: "\"unknown\""));
        Assert.Equal(404, error.StatusCode); Assert.Equal(0, store.Writes);
    }

    [Theory]
    [InlineData("missing-control")]
    [InlineData("null-pointer")]
    [InlineData("other-pointer")]
    [InlineData("invalid-control")]
    [InlineData("missing-round")]
    [InlineData("bad-round")]
    [InlineData("revision-overflow")]
    public async Task Inconsistent_storage_does_not_clear_pointer_or_partially_update(string fault)
    {
        var store = Store(); var game = GameFixture.Game(); var etag = Etag;
        switch (fault)
        {
            case "missing-control": store.Documents.Remove("control"); break;
            case "null-pointer": store.Put(GameFixture.Control(), "control"); break;
            case "other-pointer": store.Put(GameFixture.Control(Guid.NewGuid()), "control"); break;
            case "invalid-control": store.Put(GameFixture.Control(GameFixture.Id) with { OwnerKey = "other" }, "control"); break;
            case "missing-round": store.Documents.Remove($"round:{GameFixture.Id:D}:1"); break;
            case "bad-round": store.Put(GameFixture.Round() with { MinimumValue = 1 }, $"round:{GameFixture.Id:D}:1"); break;
            case "revision-overflow": store.Put(game with { Revision = long.MaxValue }, game.Id); etag = GameEtag.Create(GameFixture.Id, long.MaxValue); break;
        }
        var error = await Assert.ThrowsAsync<ApiException>(() => Cancel(store, etag: etag));
        Assert.Equal(503, error.StatusCode); Assert.Equal(0, store.Writes);
        Assert.Equal("rolling", ((GameDocument)store.Documents[game.Id].Document).Status);
        Assert.DoesNotContain(store.Documents.Keys, k => k.StartsWith("command:"));
    }

    [Fact]
    public async Task Replay_uses_success_record_even_without_game_control_round_or_roster()
    {
        var store = Store(); var key = Guid.NewGuid(); var saved = await Cancel(store, key);
        store.Documents.Clear(); store.Put(saved.Command, saved.Command.Id);
        var replay = await Cancel(store, key);
        Assert.True(replay.Replayed); Assert.Equal(saved.Command, replay.Command); Assert.Equal(1, store.Writes);
    }

    [Fact]
    public async Task Key_reuse_with_other_revision_or_target_or_operation_is_rejected()
    {
        var store = Store(); var key = Guid.NewGuid(); await Cancel(store, key);
        foreach (var run in new Func<Task<CommandResponse>>[]
        {
            () => Cancel(store, key, GameEtag.Create(GameFixture.Id, 5)),
            () => Cancel(store, key, id: Guid.NewGuid()),
            () => GameCreateTests.Create(store, key)
        })
        {
            var before = store.Reads;
            var error = await Assert.ThrowsAsync<ApiException>(run);
            Assert.Equal("IDEMPOTENCY_KEY_REUSED", error.Code); Assert.Equal(before + 1, store.Reads);
        }
        Assert.Equal(1, store.Writes);
        var other = new CreateStore(); var createKey = Guid.NewGuid(); await GameCreateTests.Create(other, createKey);
        var reverse = await Assert.ThrowsAsync<ApiException>(() => Cancel(other, createKey));
        Assert.Equal("IDEMPOTENCY_KEY_REUSED", reverse.Code);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Concurrent_cancel_same_key_replays_and_other_key_gets_412(bool sameKey)
    {
        var store = Store(); var key = Guid.NewGuid();
        store.BeforeWrite = s => { s.BeforeWrite = null; Cancel(s, sameKey ? key : Guid.NewGuid()).GetAwaiter().GetResult(); };
        if (sameKey) Assert.True((await Cancel(store, key)).Replayed);
        else Assert.Equal(412, (await Assert.ThrowsAsync<ApiException>(() => Cancel(store, key))).StatusCode);
        Assert.Equal(5, ((GameDocument)store.Documents[$"game:{GameFixture.Id:D}"].Document).Revision);
        Assert.Single(store.Documents.Keys, k => k.StartsWith("command:"));
    }

    [Fact]
    public async Task Conditional_game_conflict_does_not_follow_a_new_revision()
    {
        var store = Store(); var game = GameFixture.Game();
        store.BeforeWrite = s => { s.BeforeWrite = null; s.Put(game with { Revision = 5 }, game.Id); };
        var error = await Assert.ThrowsAsync<ApiException>(() => Cancel(store));
        Assert.Equal(412, error.StatusCode); Assert.Equal(1, store.Writes);
        Assert.Equal(GameFixture.Id, ((ControlDocument)store.Documents["control"].Document).ActiveGameId);
        Assert.DoesNotContain(store.Documents.Keys, k => k.StartsWith("command:"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Control_only_conflicts_retry_with_latest_etag_and_are_bounded(bool continuous)
    {
        var store = Store();
        store.BeforeWrite = s => { if (!continuous) s.BeforeWrite = null; s.Put(GameFixture.Control(GameFixture.Id), "control"); };
        if (!continuous) { Assert.False((await Cancel(store)).Replayed); Assert.Equal(2, store.Writes); }
        else
        {
            Assert.Equal(503, (await Assert.ThrowsAsync<ApiException>(() => Cancel(store))).StatusCode);
            Assert.Equal(3, store.Writes); Assert.Equal(4, ((GameDocument)store.Documents[$"game:{GameFixture.Id:D}"].Document).Revision);
            Assert.DoesNotContain(store.Documents.Keys, k => k.StartsWith("command:"));
        }
    }

    [Fact]
    public async Task Commit_then_transport_failure_replays_without_second_write_and_allows_new_game()
    {
        var store = Store("awaitingPayment"); var key = Guid.NewGuid(); store.LoseResponse = true;
        Assert.True((await Cancel(store, key)).Replayed); Assert.True((await Cancel(store, key)).Replayed);
        Assert.Equal(1, store.Writes); store.LoseResponse = false;
        var created = await GameCreateTests.Create(store, Guid.NewGuid());
        Assert.NotEqual(GameFixture.Id, created.Command.GameId);
        Assert.Equal(created.Command.GameId, ((ControlDocument)store.Documents["control"].Document).ActiveGameId);
        Assert.True((await Cancel(store, key)).Replayed);
        Assert.Equal(created.Command.GameId, ((ControlDocument)store.Documents["control"].Document).ActiveGameId);
    }

    [Fact]
    public async Task Concurrent_commit_after_initial_command_read_still_replays_before_version_error()
    {
        var store = Store(); var key = Guid.NewGuid(); var hook = new Mock<ICosmosPointReader>(); var first = true;
        hook.Setup(s => s.ReadAsync<CommandDocument>(DataTests.Owner, $"command:{key:D}", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(async (string owner, string id, string? session, CancellationToken token) =>
            {
                if (first) { first = false; await Cancel(store, key); return new DocumentRead<CommandDocument>(null, "0:-1#1", null); }
                return await store.ReadAsync<CommandDocument>(owner, id, session, token);
            });
        hook.Setup(s => s.ReadAsync<GameDocument>(DataTests.Owner, $"game:{GameFixture.Id:D}", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns((string owner, string id, string? session, CancellationToken token) => store.ReadAsync<GameDocument>(owner, id, session, token));
        var result = await new GameCanceller(hook.Object, store).CancelAsync(DataTests.Owner, GameFixture.Id, key, Hash(), Etag, GameReaderTests.Session(), default);
        Assert.True(result.Replayed); Assert.Equal(1, store.Writes);
    }

    [Fact]
    public async Task Update_during_round_read_is_rechecked_before_reporting_snapshot_inconsistency()
    {
        var store = Store(); var hook = new Mock<ICosmosPointReader>();
        hook.Setup(s => s.ReadAsync<CommandDocument>(DataTests.Owner, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns((string owner, string id, string? session, CancellationToken token) => store.ReadAsync<CommandDocument>(owner, id, session, token));
        hook.Setup(s => s.ReadAsync<GameDocument>(DataTests.Owner, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns((string owner, string id, string? session, CancellationToken token) => store.ReadAsync<GameDocument>(owner, id, session, token));
        hook.Setup(s => s.ReadAsync<ControlDocument>(DataTests.Owner, "control", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns((string owner, string id, string? session, CancellationToken token) => store.ReadAsync<ControlDocument>(owner, id, session, token));
        hook.Setup(s => s.ReadAsync<RoundDocument>(DataTests.Owner, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns((string owner, string id, string? session, CancellationToken token) =>
            {
                store.Put(GameFixture.Game("tie") with { Revision = 5 }, $"game:{GameFixture.Id:D}");
                return Task.FromResult(new DocumentRead<RoundDocument>(GameFixture.Round("tie"), "0:-1#3", "round-etag"));
            });
        var error = await Assert.ThrowsAsync<ApiException>(() => new GameCanceller(hook.Object, store).CancelAsync(DataTests.Owner,
            GameFixture.Id, Guid.NewGuid(), Hash(), Etag, GameReaderTests.Session(), default));
        Assert.Equal(412, error.StatusCode); Assert.Equal(0, store.Writes);
    }

    [Fact]
    public async Task Every_read_and_batch_inherit_the_latest_session_and_the_batch_uses_internal_etags()
    {
        var key = Guid.NewGuid(); var session = GameReaderTests.Session(); session.Accept("0:-1#1");
        var reader = new Mock<ICosmosPointReader>(MockBehavior.Strict); var writer = new Mock<ICosmosBatchWriter>(MockBehavior.Strict);
        reader.Setup(s => s.ReadAsync<CommandDocument>(DataTests.Owner, $"command:{key:D}", "0:-1#1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DocumentRead<CommandDocument>(null, "0:-1#2", null));
        reader.SetupSequence(s => s.ReadAsync<GameDocument>(DataTests.Owner, $"game:{GameFixture.Id:D}", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DocumentRead<GameDocument>(GameFixture.Game(), "0:-1#3", "game-internal"))
            .ReturnsAsync(new DocumentRead<GameDocument>(GameFixture.Game(), "0:-1#6", "game-internal"));
        reader.Setup(s => s.ReadAsync<RoundDocument>(DataTests.Owner, $"round:{GameFixture.Id:D}:1", "0:-1#3", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DocumentRead<RoundDocument>(GameFixture.Round(), "0:-1#4", "round-internal"));
        reader.Setup(s => s.ReadAsync<ControlDocument>(DataTests.Owner, "control", "0:-1#4", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DocumentRead<ControlDocument>(GameFixture.Control(GameFixture.Id), "0:-1#5", "control-internal"));
        writer.Setup(s => s.WriteAsync(DataTests.Owner, It.Is<IReadOnlyList<BatchWrite>>(w =>
                w.Count == 3 && w[0].Etag == "game-internal" && w[1].Etag == "control-internal" && w[2].Etag == null),
                "0:-1#6", It.IsAny<CancellationToken>())).ReturnsAsync(new BatchResult(false, "0:-1#7"));
        await new GameCanceller(reader.Object, writer.Object).CancelAsync(DataTests.Owner, GameFixture.Id, key, Hash(), Etag, session, default);
        reader.Verify(s => s.ReadAsync<GameDocument>(DataTests.Owner, $"game:{GameFixture.Id:D}", "0:-1#2", It.IsAny<CancellationToken>()), Times.Once);
        reader.Verify(s => s.ReadAsync<GameDocument>(DataTests.Owner, $"game:{GameFixture.Id:D}", "0:-1#5", It.IsAny<CancellationToken>()), Times.Once);
        writer.VerifyAll(); Assert.Equal("0:-1#7", session.Token);
    }

    [Fact]
    public async Task Failure_before_commit_has_no_success_record_and_same_key_can_be_retried()
    {
        var store = Store(); var key = Guid.NewGuid(); var writer = new Mock<ICosmosBatchWriter>();
        writer.Setup(s => s.WriteAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<BatchWrite>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(CosmosErrors.Unavailable());
        var error = await Assert.ThrowsAsync<ApiException>(() => new GameCanceller(store, writer.Object).CancelAsync(DataTests.Owner,
            GameFixture.Id, key, Hash(), Etag, GameReaderTests.Session(), default));
        Assert.Equal(503, error.StatusCode); Assert.Equal(4, ((GameDocument)store.Documents[$"game:{GameFixture.Id:D}"].Document).Revision);
        Assert.DoesNotContain(store.Documents.Keys, k => k.StartsWith("command:"));
        Assert.False((await Cancel(store, key)).Replayed);
    }

    [Fact]
    public async Task Request_cancellation_is_propagated_without_DB_access()
    {
        var store = Store(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GameCanceller(store, store).CancelAsync(DataTests.Owner,
            GameFixture.Id, Guid.NewGuid(), Hash(), Etag, GameReaderTests.Session(), cancellation.Token));
        Assert.Equal(0, store.Reads); Assert.Equal(0, store.Writes);
    }
}
