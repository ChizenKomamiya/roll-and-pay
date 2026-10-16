using System.Text.Json;
using Moq;
using RollAndPay.Api.Data;
using RollAndPay.Api.Games;
using RollAndPay.Api.History;
using RollAndPay.Api.Http;
using RollAndPay.Api.Payments;
using Xunit;

namespace RollAndPay.Api.Tests;

public sealed class HistoryDeleteTests
{
    internal static string Etag => GameEtag.Create(GameFixture.Id, 5);
    internal static string Hash(string? etag = null, Guid? id = null) =>
        UpdateRequest.Hash("DELETE", $"/api/v1/games/{id ?? GameFixture.Id:D}", null, etag ?? Etag);
    internal static async Task<CreateStore> Store(long total = 3800)
    {
        var store = PaymentSaveTests.Store(); await PaymentSaveTests.Save(store, total: total);
        store.Writes = store.Reads = 0;
        return store;
    }
    internal static Task<CommandResponse> Delete(CreateStore store, Guid? key = null, string? etag = null, Guid? id = null) =>
        new HistoryDeleter(store, store).DeleteAsync(DataTests.Owner, id ?? GameFixture.Id, key ?? Guid.NewGuid(),
            Hash(etag, id), etag ?? Etag, GameReaderTests.Session(), default);
    internal static GameDocument Game(CreateStore store, Guid? id = null) => (GameDocument)store.Documents[$"game:{id ?? GameFixture.Id:D}"].Document;

    [Theory]
    [InlineData(3800, 1800)] [InlineData(4000, 0)] [InlineData(1, 1)]
    public async Task Deletes_saved_L_once_preserving_payment_names_rounds_and_unrelated_active_pointer(long total, long loss)
    {
        var store = await Store(total); var key = Guid.NewGuid(); var game = Game(store);
        var totals = PaymentSaveTests.Totals(store);
        store.Put(totals with { Totals = totals.Totals.Select(t => t with { TotalLossYen = "9007199254740993" }).ToArray() }, "lossTotals");
        store.Put(GameFixture.Control(Guid.NewGuid()), "control");
        var control = store.Documents["control"]; var roster = store.Documents["roster"];
        var round = store.Documents[$"round:{GameFixture.Id:D}:1"];
        var result = await Delete(store, key);
        Assert.False(result.Replayed); Assert.Equal("deleteHistory", result.Command.Operation);
        Assert.Equal(200, result.Command.ResponseStatus); Assert.Null(result.Command.ResponseLocation);
        Assert.Equal(GameEtag.Create(GameFixture.Id, 6), result.Command.ResponseEtag);
        Assert.Equal(new[] { game.Id, "lossTotals", $"command:{key:D}" }, store.LastBatch!.Select(w => w.Id));
        Assert.Equal(new[] { true, true, false }, store.LastBatch!.Select(w => w.Etag is not null));
        var deleted = Game(store);
        Assert.Equal("deleted", deleted.Status); Assert.Equal(6, deleted.Revision);
        Assert.Equal(game, deleted with { Status = game.Status, Revision = game.Revision, UpdatedAt = game.UpdatedAt, DeletedAt = null });
        Assert.NotNull(deleted.DeletedAt); Assert.Equal(deleted.UpdatedAt, deleted.DeletedAt);
        Assert.Equal(deleted.DeletedAt, PaymentSaveTests.Totals(store).UpdatedAt);
        Assert.Equal(9007199254740993L - loss, PaymentSaveTests.Amount(store));
        Assert.All(PaymentSaveTests.Totals(store).Totals.Where(t => t.ParticipantId != "p001"), t => Assert.Equal("9007199254740993", t.TotalLossYen));
        Assert.Equal(control, store.Documents["control"]); Assert.Equal(roster, store.Documents["roster"]); Assert.Equal(round, store.Documents[$"round:{GameFixture.Id:D}:1"]);
        Assert.Equal(loss, result.Command.ResponseBody.GetProperty("removedLoss").GetProperty("amountYen").GetInt64());
        Assert.Equal(result.Command.ResponseBody.GetRawText(), (await Delete(store, key)).Command.ResponseBody.GetRawText());
        Assert.Equal(1, store.Writes);
        Assert.Equal(410, (await Assert.ThrowsAsync<ApiException>(() => Delete(store))).StatusCode);
    }

    [Theory]
    [InlineData("rolling", false, 409)] [InlineData("tie", false, 409)]
    [InlineData("awaitingPayment", false, 409)] [InlineData("cancelled", false, 409)]
    [InlineData("completed", true, 412)] [InlineData("cancelled", true, 412)]
    [InlineData("deleted", false, 410)] [InlineData("deleted", true, 410)]
    public async Task State_and_ETag_errors_leave_all_documents_unchanged(string status, bool stale, int expected)
    {
        var store = PaymentSaveTests.Store(status); var before = store.Documents.ToArray();
        var error = await Assert.ThrowsAsync<ApiException>(() => Delete(store, etag: stale ? "\"old\"" : PaymentSaveTests.Etag));
        Assert.Equal(expected, error.StatusCode); Assert.Equal(0, store.Writes); Assert.Equal(before, store.Documents.ToArray());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Missing_or_foreign_game_is_404(bool foreign)
    {
        var store = await Store(); var game = Game(store);
        if (foreign) store.Put(game with { OwnerKey = "other" }, game.Id); else store.Documents.Remove(game.Id);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => Delete(store))).StatusCode); Assert.Equal(0, store.Writes);
    }

    [Theory]
    [InlineData("totals")] [InlineData("roster")] [InlineData("negative")] [InlineData("revision")]
    [InlineData("payment")] [InlineData("missing-loser")] [InlineData("future")]
    public async Task Corrupt_or_insufficient_aggregate_and_invalid_saved_game_abort_without_writes(string fault)
    {
        var store = await Store(); var game = Game(store); var etag = Etag;
        switch (fault)
        {
            case "totals": store.Documents.Remove("lossTotals"); break;
            case "roster": store.Documents.Remove("roster"); break;
            case "negative": store.Put(PaymentSaveTests.Totals(store) with { Totals = PaymentSaveTests.Totals(store).Totals.Select(t => t with { TotalLossYen = "1799" }).ToArray() }, "lossTotals"); break;
            case "revision": store.Put(game with { Revision = long.MaxValue }, game.Id); etag = GameEtag.Create(game.GameId, long.MaxValue); break;
            case "payment": store.Put(game with { Payment = game.Payment! with { LossYen = -1 } }, game.Id); break;
            case "future": store.Put(PaymentSaveTests.Totals(store) with { UpdatedAt = DateTimeOffset.UtcNow.AddDays(1) }, "lossTotals"); break;
            case "missing-loser":
                var roster = (RosterDocument)store.Documents["roster"].Document;
                store.Put(roster with { Participants = roster.Participants.Where(p => p.ParticipantId != "p001").ToArray() }, "roster");
                store.Put(PaymentSaveTests.Totals(store) with { Totals = PaymentSaveTests.Totals(store).Totals.Where(t => t.ParticipantId != "p001").ToArray() }, "lossTotals"); break;
        }
        var before = store.Documents.ToArray();
        Assert.Equal(503, (await Assert.ThrowsAsync<ApiException>(() => Delete(store, etag: etag))).StatusCode);
        Assert.Equal(0, store.Writes); Assert.Equal(before, store.Documents.ToArray());
    }

    [Fact]
    public async Task Stored_historical_loss_is_used_without_reapplying_current_calculation_and_no_control_or_round_read_is_needed()
    {
        var store = await Store(); var game = Game(store);
        // An older calculation policy allocated all of this two-person payment to the loser.
        var payment = game.Payment! with { BaseYen = 0, LossYen = 3800, LoserPaymentYen = 3800, CollectFromOthersYen = 0,
            Shares = game.Payment.Shares.Select(s => s with { AmountYen = s.ParticipantId == "p001" ? 3800 : 0 }).ToArray(), CalculationVersion = "historical" };
        store.Put(game with { Payment = payment }, game.Id);
        store.Put(PaymentSaveTests.Totals(store) with { Totals = PaymentSaveTests.Totals(store).Totals.Select(t => t with { TotalLossYen = "3800" }).ToArray() }, "lossTotals");
        store.Documents.Remove("control"); store.Documents.Remove($"round:{GameFixture.Id:D}:1");
        var deleted = await Delete(store);
        Assert.Equal(3800, deleted.Command.ResponseBody.GetProperty("removedLoss").GetProperty("amountYen").GetInt64());
        Assert.Equal(0, PaymentSaveTests.Amount(store));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Same_game_concurrent_deletes_replay_same_key_or_return_410_for_other_key(bool sameKey)
    {
        var store = await Store(); var key = Guid.NewGuid();
        store.BeforeWrite = s => { s.BeforeWrite = null; Delete(s, sameKey ? key : Guid.NewGuid()).GetAwaiter().GetResult(); };
        if (sameKey) Assert.True((await Delete(store, key)).Replayed);
        else Assert.Equal(410, (await Assert.ThrowsAsync<ApiException>(() => Delete(store, key))).StatusCode);
        Assert.Equal(0, PaymentSaveTests.Amount(store)); Assert.Equal("deleted", Game(store).Status);
        Assert.Single(store.Documents.Values, d => d.Document is CommandDocument c && c.Operation == "deleteHistory");
    }

    [Fact]
    public async Task Different_history_deletions_retry_totals_conflict_and_each_subtract_once()
    {
        var store = await Store(); var other = Guid.NewGuid(); var game = Game(store);
        store.Put(game with { Id = $"game:{other:D}", GameId = other }, $"game:{other:D}");
        store.Put(PaymentSaveTests.Totals(store) with { Totals = PaymentSaveTests.Totals(store).Totals.Select(t => t.ParticipantId == "p001" ? t with { TotalLossYen = "3600" } : t).ToArray() }, "lossTotals");
        store.BeforeWrite = s => { s.BeforeWrite = null; Delete(s, etag: GameEtag.Create(other, 5), id: other).GetAwaiter().GetResult(); };
        await Delete(store);
        Assert.Equal(0, PaymentSaveTests.Amount(store)); Assert.Equal("deleted", Game(store).Status); Assert.Equal("deleted", Game(store, other).Status);
        Assert.Equal(3, store.Writes);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Deletion_and_new_game_save_retry_totals_conflict_and_keep_both_results(bool deleteWins)
    {
        var store = await Store(); var other = Guid.NewGuid();
        store.Put(GameFixture.Game("awaitingPayment") with { Id = $"game:{other:D}", GameId = other }, $"game:{other:D}");
        store.Put(GameFixture.Round("awaitingPayment") with { Id = $"round:{other:D}:1", GameId = other }, $"round:{other:D}:1");
        store.Put(GameFixture.Control(other), "control");
        Task<CommandResponse> Save(CreateStore s) => PaymentSaveTests.Save(s, etag: GameEtag.Create(other, 4), id: other);
        store.BeforeWrite = s => { s.BeforeWrite = null; (deleteWins ? Delete(s) : Save(s)).GetAwaiter().GetResult(); };
        await (deleteWins ? Save(store) : Delete(store));
        Assert.Equal(1800, PaymentSaveTests.Amount(store)); Assert.Equal("deleted", Game(store).Status); Assert.Equal("completed", Game(store, other).Status);
        Assert.Null(((ControlDocument)store.Documents["control"].Document).ActiveGameId);
    }

    [Fact]
    public async Task Continuing_conflicts_stop_after_three_batches_without_partial_deletion()
    {
        var store = await Store(); var before = Game(store);
        store.BeforeWrite = s => s.Put(PaymentSaveTests.Totals(s), "lossTotals");
        Assert.Equal(503, (await Assert.ThrowsAsync<ApiException>(() => Delete(store))).StatusCode);
        Assert.Equal(3, store.Writes); Assert.Equal(before, Game(store)); Assert.Equal(1800, PaymentSaveTests.Amount(store));
        Assert.DoesNotContain(store.Documents.Values, d => d.Document is CommandDocument c && c.Operation == "deleteHistory");
    }

    [Fact]
    public async Task Lost_response_and_old_save_replay_after_delete_never_restore_or_readd_and_delete_replays_without_current_documents()
    {
        var store = PaymentSaveTests.Store(); var saveKey = Guid.NewGuid(); var saved = await PaymentSaveTests.Save(store, saveKey);
        var key = Guid.NewGuid(); store.LoseResponse = true;
        var deleted = await Delete(store, key); Assert.True(deleted.Replayed); Assert.Equal(0, PaymentSaveTests.Amount(store));
        store.LoseResponse = false; var before = store.Documents.ToArray();
        Assert.Equal(saved.Command.ResponseBody.GetRawText(), (await PaymentSaveTests.Save(store, saveKey)).Command.ResponseBody.GetRawText());
        Assert.Equal(410, (await Assert.ThrowsAsync<ApiException>(() => PaymentSaveTests.Save(store))).StatusCode);
        Assert.Equal(before, store.Documents.ToArray());
        store.Documents.Clear(); store.Put(deleted.Command, deleted.Command.Id);
        Assert.True((await Delete(store, key)).Replayed);
    }

    [Fact]
    public async Task Key_reuse_for_different_target_version_or_operation_is_rejected()
    {
        var store = await Store(); var key = Guid.NewGuid(); await Delete(store, key);
        foreach (var run in new Func<Task<CommandResponse>>[] { () => Delete(store, key, id: Guid.NewGuid()),
            () => Delete(store, key, etag: "\"old\""), () => PaymentSaveTests.Save(store, key) })
            Assert.Equal("IDEMPOTENCY_KEY_REUSED", (await Assert.ThrowsAsync<ApiException>(run)).Code);
        Assert.Equal(1, store.Writes);
    }

    [Fact]
    public async Task Failure_before_commit_changes_nothing_and_original_key_can_retry()
    {
        var store = await Store(); var before = store.Documents.ToArray(); var key = Guid.NewGuid();
        var writer = new Mock<ICosmosBatchWriter>();
        writer.Setup(w => w.WriteAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<BatchWrite>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ThrowsAsync(CosmosErrors.Unavailable());
        Assert.Equal(503, (await Assert.ThrowsAsync<ApiException>(() => new HistoryDeleter(store, writer.Object).DeleteAsync(DataTests.Owner,
            GameFixture.Id, key, Hash(), Etag, GameReaderTests.Session(), default))).StatusCode);
        Assert.Equal(before, store.Documents.ToArray()); Assert.False((await Delete(store, key)).Replayed);
    }
}
