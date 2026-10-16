using System.Text.Json;
using Moq;
using RollAndPay.Api.Data;
using RollAndPay.Api.Games;
using RollAndPay.Api.Http;
using RollAndPay.Api.Payments;
using RollAndPay.Seed;
using Xunit;

namespace RollAndPay.Api.Tests;

public sealed class PaymentSaveTests
{
    internal static string Etag => GameCancelTests.Etag;
    internal static string Hash(long total = 3800, string? etag = null, Guid? id = null) => UpdateRequest.Hash("POST",
        $"/api/v1/games/{id ?? GameFixture.Id:D}/payment", JsonSerializer.SerializeToElement(new { totalYen = total }), etag ?? Etag);
    internal static CreateStore Store(string status = "awaitingPayment")
    {
        var store = GameCancelTests.Store(status);
        var roster = (RosterDocument)store.Documents["roster"].Document;
        store.Put(InitialData.Build(DataTests.Owner, roster.Participants, GameFixture.Now).LossTotals, "lossTotals");
        return store;
    }
    internal static Task<CommandResponse> Save(CreateStore store, Guid? key = null, long total = 3800, string? etag = null, Guid? id = null) =>
        new PaymentSaver(store, store).SaveAsync(DataTests.Owner, id ?? GameFixture.Id, key ?? Guid.NewGuid(), Hash(total, etag, id),
            etag ?? Etag, total, GameReaderTests.Session(), default);
    internal static LossTotalsDocument Totals(CreateStore store) => (LossTotalsDocument)store.Documents["lossTotals"].Document;
    internal static long Amount(CreateStore store) => LossTotalsReader.Parse(Totals(store).Totals.Single(t => t.ParticipantId == "p001").TotalLossYen);

    [Theory]
    [InlineData(3800, 1800)]
    [InlineData(4000, 0)]
    [InlineData(1, 1)]
    [InlineData(9999999, 1999)]
    public async Task Saves_recalculated_payment_adds_only_L_and_preserves_round_names_and_other_totals(long total, long loss)
    {
        var store = Store(); var key = Guid.NewGuid();
        var round = store.Documents[$"round:{GameFixture.Id:D}:1"]; var roster = store.Documents["roster"];
        var original = Totals(store); store.Put(original with { Totals = original.Totals.Select(t => t with { TotalLossYen = "9007199254740993" }).ToArray(), UpdatedAt = GameFixture.Now }, "lossTotals");
        var result = await Save(store, key, total);
        Assert.False(result.Replayed); Assert.Equal(200, result.Command.ResponseStatus);
        Assert.Equal(GameEtag.Create(GameFixture.Id, 5), result.Command.ResponseEtag);
        Assert.Equal(4, store.LastBatch!.Count); Assert.Equal(new[] { true, true, true, false }, store.LastBatch.Select(w => w.Etag is not null));
        var game = (GameDocument)store.Documents[$"game:{GameFixture.Id:D}"].Document;
        Assert.Equal("completed", game.Status); Assert.Equal(5, game.Revision); Assert.Equal(total, game.Payment!.TotalYen);
        Assert.Equal(loss, game.Payment.LossYen); Assert.Equal(game.CompletedAt, game.Payment.RegisteredAt);
        Assert.Equal(game.CompletedAt, game.UpdatedAt); Assert.Equal(game.CompletedAt, Totals(store).UpdatedAt);
        Assert.Equal(9007199254740993L + loss, Amount(store));
        Assert.All(Totals(store).Totals.Where(t => t.ParticipantId != "p001"), t => Assert.Equal("9007199254740993", t.TotalLossYen));
        Assert.Equal(round, store.Documents[$"round:{GameFixture.Id:D}:1"]); Assert.Equal(roster, store.Documents["roster"]);
        Assert.Equal("当時の名前A", game.Participants[0].DisplayName);
        Assert.Null(((ControlDocument)store.Documents["control"].Document).ActiveGameId);
        Assert.Null(await new GameReader(store).GetActiveAsync(DataTests.Owner, GameReaderTests.Session(), default));
        Assert.Equal("completed", (await new GameReader(store).GetAsync(DataTests.Owner, GameFixture.Id, GameReaderTests.Session(), default)).Status);
        var body = result.Command.ResponseBody;
        Assert.Equal("completed", body.GetProperty("game").GetProperty("status").GetString());
        var current = body.GetProperty("lossTotals").GetProperty("participants").EnumerateArray().Single(p => p.GetProperty("participantId").GetString() == "p001");
        Assert.Equal(((RosterDocument)roster.Document).Participants[0].DisplayName, current.GetProperty("displayName").GetString());
        Assert.Equal(JsonValueKind.String, current.GetProperty("totalLossYen").ValueKind);
        Assert.True((await Save(store, key, total)).Replayed); Assert.Equal(1, store.Writes);
    }

    [Theory]
    [InlineData("rolling", false, 409)] [InlineData("tie", false, 409)]
    [InlineData("completed", false, 409)] [InlineData("cancelled", false, 409)]
    [InlineData("completed", true, 412)] [InlineData("cancelled", true, 412)]
    [InlineData("deleted", true, 410)] [InlineData("deleted", false, 410)]
    public async Task State_and_version_boundaries_do_not_change_aggregate(string state, bool stale, int status)
    {
        var store = Store(state); var totals = store.Documents["lossTotals"];
        Assert.Equal(status, (await Assert.ThrowsAsync<ApiException>(() => Save(store, etag: stale ? "\"old\"" : Etag))).StatusCode);
        Assert.Equal(0, store.Writes); Assert.Equal(totals, store.Documents["lossTotals"]);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Foreign_and_missing_game_are_404(bool foreign)
    {
        var store = Store(); var id = $"game:{GameFixture.Id:D}";
        if (foreign) store.Put(GameFixture.Game("awaitingPayment") with { OwnerKey = "other" }, id); else store.Documents.Remove(id);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => Save(store))).StatusCode); Assert.Equal(0, store.Writes);
    }

    [Theory]
    [InlineData("totals")] [InlineData("roster")] [InlineData("control")] [InlineData("round")]
    [InlineData("pointer")] [InlineData("revision")] [InlineData("overflow")] [InlineData("missing-participant")]
    public async Task Missing_or_inconsistent_documents_abort_without_any_writes(string fault)
    {
        var store = Store(); var etag = Etag;
        switch (fault)
        {
            case "totals": store.Documents.Remove("lossTotals"); break;
            case "roster": store.Documents.Remove("roster"); break;
            case "control": store.Documents.Remove("control"); break;
            case "round": store.Documents.Remove($"round:{GameFixture.Id:D}:1"); break;
            case "pointer": store.Put(GameFixture.Control(Guid.NewGuid()), "control"); break;
            case "revision": store.Put(GameFixture.Game("awaitingPayment") with { Revision = long.MaxValue }, $"game:{GameFixture.Id:D}"); etag = GameEtag.Create(GameFixture.Id, long.MaxValue); break;
            case "overflow": store.Put(Totals(store) with { Totals = Totals(store).Totals.Select(t => t with { TotalLossYen = long.MaxValue.ToString() }).ToArray(), UpdatedAt = GameFixture.Now }, "lossTotals"); break;
            case "missing-participant":
                var roster = (RosterDocument)store.Documents["roster"].Document;
                store.Put(roster with { Participants = roster.Participants.Where(p => p.ParticipantId != "p001").ToArray() }, "roster");
                store.Put(Totals(store) with { Totals = Totals(store).Totals.Where(t => t.ParticipantId != "p001").ToArray() }, "lossTotals"); break;
        }
        Assert.Equal(503, (await Assert.ThrowsAsync<ApiException>(() => Save(store, etag: etag))).StatusCode);
        Assert.Equal(0, store.Writes); Assert.DoesNotContain(store.Documents.Keys, k => k.StartsWith("command:"));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Concurrent_saves_same_key_replay_and_different_keys_conflict_without_double_add(bool same)
    {
        var store = Store(); var key = Guid.NewGuid();
        store.BeforeWrite = s => { s.BeforeWrite = null; Save(s, same ? key : Guid.NewGuid()).GetAwaiter().GetResult(); };
        if (same) Assert.True((await Save(store, key)).Replayed);
        else Assert.Equal(412, (await Assert.ThrowsAsync<ApiException>(() => Save(store, key))).StatusCode);
        Assert.Equal(1800, Amount(store)); Assert.Single(store.Documents.Keys, k => k.StartsWith("command:"));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Save_and_cancel_races_preserve_winner_and_all_or_nothing_totals(bool saveWins)
    {
        var store = Store();
        store.BeforeWrite = s => { s.BeforeWrite = null; (saveWins ? Save(s) : GameCancelTests.Cancel(s)).GetAwaiter().GetResult(); };
        var error = await Assert.ThrowsAsync<ApiException>(() => saveWins ? GameCancelTests.Cancel(store) : Save(store));
        Assert.Equal(412, error.StatusCode); Assert.Equal(saveWins ? 1800 : 0, Amount(store));
        Assert.Equal(saveWins ? "completed" : "cancelled", ((GameDocument)store.Documents[$"game:{GameFixture.Id:D}"].Document).Status);
        Assert.Null(((ControlDocument)store.Documents["control"].Document).ActiveGameId);
        Assert.Single(store.Documents.Keys, k => k.StartsWith("command:"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Totals_only_conflicts_recalculate_from_latest_and_stop_after_three_batches(bool continuous)
    {
        var store = Store();
        store.BeforeWrite = s => { if (!continuous) s.BeforeWrite = null; s.Put(Totals(s) with { Totals = Totals(s).Totals.Select(t => t with { TotalLossYen = "2000" }).ToArray(), UpdatedAt = GameFixture.Now }, "lossTotals"); };
        if (!continuous) { await Save(store); Assert.Equal(3800, Amount(store)); Assert.Equal(2, store.Writes); }
        else
        {
            Assert.Equal(503, (await Assert.ThrowsAsync<ApiException>(() => Save(store))).StatusCode);
            Assert.Equal(3, store.Writes); Assert.Equal("awaitingPayment", ((GameDocument)store.Documents[$"game:{GameFixture.Id:D}"].Document).Status);
            Assert.Equal(GameFixture.Id, ((ControlDocument)store.Documents["control"].Document).ActiveGameId);
            Assert.DoesNotContain(store.Documents.Keys, k => k.StartsWith("command:"));
        }
    }

    [Fact]
    public async Task Lost_response_and_replay_after_deletion_or_new_game_never_readd_or_restore()
    {
        var store = Store(); var key = Guid.NewGuid(); store.LoseResponse = true;
        var saved = await Save(store, key); Assert.True(saved.Replayed); Assert.Equal(1, store.Writes);
        store.LoseResponse = false;
        var game = (GameDocument)store.Documents[$"game:{GameFixture.Id:D}"].Document;
        store.Put(game with { Status = "deleted", DeletedAt = game.UpdatedAt, Revision = 6 }, game.Id);
        store.Put(Totals(store) with { Totals = Totals(store).Totals.Select(t => t with { TotalLossYen = "0" }).ToArray() }, "lossTotals");
        var created = await GameCreateTests.Create(store, Guid.NewGuid());
        var replay = await Save(store, key); Assert.Equal(saved.Command.ResponseBody.GetRawText(), replay.Command.ResponseBody.GetRawText());
        Assert.Equal(0, Amount(store)); Assert.Equal("deleted", ((GameDocument)store.Documents[game.Id].Document).Status);
        Assert.Equal(created.Command.GameId, ((ControlDocument)store.Documents["control"].Document).ActiveGameId);
        store.Documents.Clear(); store.Put(saved.Command, saved.Command.Id);
        Assert.True((await Save(store, key)).Replayed);
    }

    [Fact]
    public async Task Reusing_key_for_other_amount_revision_target_or_operation_is_rejected()
    {
        var store = Store(); var key = Guid.NewGuid(); await Save(store, key);
        foreach (var run in new Func<Task<CommandResponse>>[] { () => Save(store, key, 1), () => Save(store, key, etag: "\"other\""),
            () => Save(store, key, id: Guid.NewGuid()), () => GameCancelTests.Cancel(store, key) })
            Assert.Equal("IDEMPOTENCY_KEY_REUSED", (await Assert.ThrowsAsync<ApiException>(run)).Code);
        Assert.Equal(1, store.Writes);
    }

    [Fact]
    public async Task Precommit_failure_leaves_all_documents_unchanged_and_same_key_can_retry()
    {
        var store = Store(); var key = Guid.NewGuid(); var before = store.Documents.ToArray();
        var writer = new Mock<ICosmosBatchWriter>();
        writer.Setup(w => w.WriteAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<BatchWrite>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ThrowsAsync(CosmosErrors.Unavailable());
        Assert.Equal(503, (await Assert.ThrowsAsync<ApiException>(() => new PaymentSaver(store, writer.Object).SaveAsync(DataTests.Owner,
            GameFixture.Id, key, Hash(), Etag, 3800, GameReaderTests.Session(), default))).StatusCode);
        Assert.Equal(before, store.Documents.ToArray()); Assert.False((await Save(store, key)).Replayed);
    }
}
