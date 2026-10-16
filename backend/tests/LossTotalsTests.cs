using Moq;
using RollAndPay.Api.Data;
using RollAndPay.Api.Http;
using RollAndPay.Api.Payments;
using RollAndPay.Seed;
using Xunit;

namespace RollAndPay.Api.Tests;

public sealed class LossTotalsTests
{
    [Fact]
    public async Task Initial_totals_include_inactive_current_names_in_stable_order_and_do_not_read_games()
    {
        var store = PaymentSaveTests.Store();
        var result = await new LossTotalsReader(store).GetAsync(DataTests.Owner, GameReaderTests.Session(), default);
        Assert.Equal(new[] { "p002", "inactive", "p001" }, result.Participants.Select(p => p.ParticipantId));
        Assert.False(result.Participants[1].IsActive); Assert.All(result.Participants, p => Assert.Equal("0", p.TotalLossYen));
        Assert.Null(result.UpdatedAt); Assert.Equal(3, store.Reads); Assert.Equal(0, store.Writes);
        var roster = (RosterDocument)store.Documents["roster"].Document;
        store.Put(roster with { Participants = roster.Participants.Select(p => p with { DisplayName = "現在名", IsActive = false }).ToArray() }, "roster");
        await PaymentSaveTests.Save(store);
        var saved = await new LossTotalsReader(store).GetAsync(DataTests.Owner, GameReaderTests.Session(), default);
        Assert.All(saved.Participants, p => { Assert.False(p.IsActive); Assert.Equal("現在名", p.DisplayName); });
        Assert.Equal("1800", saved.Participants.Single(p => p.ParticipantId == "p001").TotalLossYen);
    }

    [Theory]
    [InlineData("")] [InlineData("01")] [InlineData("-1")] [InlineData("+1")] [InlineData("1.0")]
    [InlineData("1e3")] [InlineData(" 1")] [InlineData("１")] [InlineData("9223372036854775808")]
    [InlineData(null)]
    public async Task Invalid_stored_amounts_are_503(string? amount)
    {
        var store = PaymentSaveTests.Store(); var totals = PaymentSaveTests.Totals(store);
        store.Put(totals with { Totals = [new("p001", amount!), .. totals.Totals.Skip(1)], UpdatedAt = GameFixture.Now }, "lossTotals");
        Assert.Equal(503, (await Assert.ThrowsAsync<ApiException>(() => new LossTotalsReader(store).GetAsync(DataTests.Owner, GameReaderTests.Session(), default))).StatusCode);
        Assert.Equal(503, (await Assert.ThrowsAsync<ApiException>(() => PaymentSaveTests.Save(store))).StatusCode); Assert.Equal(0, store.Writes);
    }

    [Theory]
    [InlineData("missing")] [InlineData("foreign")] [InlineData("schema")] [InlineData("type")]
    [InlineData("duplicate")] [InlineData("unknown")] [InlineData("null-entry")] [InlineData("null-array")]
    [InlineData("null-time-nonzero")] [InlineData("default-time")] [InlineData("offset-time")]
    public async Task Invalid_aggregate_is_unavailable_instead_of_fabricating_zero(string fault)
    {
        var store = PaymentSaveTests.Store(); var totals = PaymentSaveTests.Totals(store);
        if (fault == "missing") store.Documents.Remove("lossTotals");
        else store.Put(fault switch
        {
            "foreign" => totals with { OwnerKey = "other" }, "schema" => totals with { SchemaVersion = 2 },
            "type" => totals with { Type = "other" }, "duplicate" => totals with { Totals = [totals.Totals[0], totals.Totals[0], totals.Totals[2]] },
            "unknown" => totals with { Totals = [new("unknown", "0"), .. totals.Totals.Skip(1)] },
            "null-entry" => totals with { Totals = [null!, .. totals.Totals.Skip(1)] }, "null-array" => totals with { Totals = null! },
            "null-time-nonzero" => totals with { Totals = [new("p001", "1"), .. totals.Totals.Skip(1)] },
            "default-time" => totals with { UpdatedAt = default(DateTimeOffset) },
            _ => totals with { UpdatedAt = GameFixture.Now.ToOffset(TimeSpan.FromHours(9)) }
        }, "lossTotals");
        Assert.Equal(503, (await Assert.ThrowsAsync<ApiException>(() => new LossTotalsReader(store).GetAsync(DataTests.Owner, GameReaderTests.Session(), default))).StatusCode);
        Assert.Equal(0, store.Writes);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Roster_changes_are_retried_and_continuous_changes_are_bounded(bool continuous)
    {
        var store = PaymentSaveTests.Store(); var reader = new Mock<ICosmosPointReader>(); var calls = 0;
        reader.Setup(r => r.ReadAsync<RosterDocument>(DataTests.Owner, "roster", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns((string owner, string id, string? session, CancellationToken token) =>
            {
                calls++;
                if (calls % 2 == 0 && (continuous || calls == 2)) store.Put(((RosterDocument)store.Documents["roster"].Document) with
                    { Participants = [DataTests.Person("p001", "新しい名前"), DataTests.Person("p002"), DataTests.Person("inactive", active: false)] }, "roster");
                return store.ReadAsync<RosterDocument>(owner, id, session, token);
            });
        reader.Setup(r => r.ReadAsync<LossTotalsDocument>(DataTests.Owner, "lossTotals", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns((string owner, string id, string? session, CancellationToken token) => store.ReadAsync<LossTotalsDocument>(owner, id, session, token));
        var service = new LossTotalsReader(reader.Object);
        if (continuous) Assert.Equal(503, (await Assert.ThrowsAsync<ApiException>(() => service.GetAsync(DataTests.Owner, GameReaderTests.Session(), default))).StatusCode);
        else Assert.Equal("新しい名前", (await service.GetAsync(DataTests.Owner, GameReaderTests.Session(), default)).Participants.Single(p => p.ParticipantId == "p001").DisplayName);
        Assert.Equal(continuous ? 6 : 4, calls);
    }

    [Fact]
    public void Empty_roster_is_valid_and_legacy_initial_timestamp_is_preserved()
    {
        var initial = InitialData.Build(DataTests.Owner, [], GameFixture.Now);
        Assert.Empty(LossTotalsReader.Compose(initial.Roster, initial.LossTotals, DataTests.Owner).Participants);
        Assert.Equal(GameFixture.Now, LossTotalsReader.Compose(initial.Roster, initial.LossTotals with { UpdatedAt = GameFixture.Now }, DataTests.Owner).UpdatedAt);
    }
}
