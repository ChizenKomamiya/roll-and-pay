using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using RollAndPay.Api.Data;
using RollAndPay.Api.Functions;
using RollAndPay.Api.Games;
using RollAndPay.Api.Http;
using Xunit;

namespace RollAndPay.Api.Tests;

internal sealed class SequenceDie(params int[] values) : IDie
{
    private readonly Queue<int> values = new(values);
    internal int Calls;
    public int Roll() { Calls++; return values.Dequeue(); }
}

public sealed class GameDiceTests(TokenFixture tokens) : IClassFixture<TokenFixture>
{
    internal static CreateStore Store(string status = "rolling")
    {
        var store = new CreateStore();
        store.Put(GameFixture.Game(status), $"game:{GameFixture.Id:D}");
        store.Put(GameFixture.Round(status), $"round:{GameFixture.Id:D}:1");
        return store;
    }
    internal static string Etag(long revision = 4) => GameEtag.Create(GameFixture.Id, revision);
    internal static Task<CommandResponse> Run(CreateStore store, IDie die, Guid? key = null, DiceRequest? request = null, bool next = false, string? etag = null)
    {
        request ??= new(1, "remaining");
        var match = etag ?? Etag();
        using var body = JsonDocument.Parse(next ? $"{{\"previousRoundNumber\":{request.RoundNumber}}}" :
            JsonSerializer.Serialize(new { roundNumber = request.RoundNumber, mode = request.Mode, participantId = request.ParticipantId }));
        return new GameDice(store, store, die).ExecuteAsync(DataTests.Owner, GameFixture.Id, key ?? Guid.NewGuid(),
            UpdateRequest.Hash("POST", $"/api/v1/games/{GameFixture.Id:D}/{(next ? "rounds" : "rolls")}", body.RootElement, match),
            match, request, next, GameReaderTests.Session(), default);
    }
    private static GameDocument Game(CreateStore store) => (GameDocument)store.Documents[$"game:{GameFixture.Id:D}"].Document;
    private static RoundDocument Round(CreateStore store, int number = 1) => (RoundDocument)store.Documents[$"round:{GameFixture.Id:D}:{number}"].Document;

    [Fact]
    public async Task Individual_then_remaining_keeps_values_and_timestamps_and_waits_for_everyone()
    {
        var store = Store(); var original = Round(store).Rolls[0];
        var game = Game(store);
        store.Put(game with { Participants = [..game.Participants, new() { ParticipantId = "p003", DisplayName = "C", Order = 3 }] }, game.Id);
        store.Put(Round(store) with { Rolls = [..Round(store).Rolls, new() { ParticipantId = "p003", Value = null, RolledAt = null }] }, Round(store).Id);
        var die = new SequenceDie(3, 3);
        var partial = await Run(store, die, request: new(1, "individual", "p002"));
        Assert.Equal(200, partial.Command.ResponseStatus); Assert.Equal("rolling", Game(store).Status);
        Assert.Null(Round(store).MinimumValue); Assert.Null(Round(store).CompletedAt); Assert.Null(Game(store).LoserParticipantId);
        var individual = Round(store).Rolls[1];
        await Run(store, die, etag: Etag(5));
        Assert.Equal(2, die.Calls); Assert.Equal(original, Round(store).Rolls[0]); Assert.Equal(individual, Round(store).Rolls[1]);
        Assert.Equal("awaitingPayment", Game(store).Status); Assert.Equal("p001", Game(store).LoserParticipantId);
        Assert.Equal("decided", Round(store).Status); Assert.Equal(1, Round(store).MinimumValue); Assert.Empty(Round(store).TiedParticipantIds);
        Assert.Equal(6, Game(store).Revision); Assert.True(GameReadValidation.Snapshot(Game(store), Round(store), DataTests.Owner));
        Assert.Equal(3, store.LastBatch!.Count); Assert.All(store.LastBatch.Take(2), w => Assert.NotNull(w.Etag));
        Assert.Null(store.LastBatch[2].Etag);
    }

    [Fact]
    public async Task Tie_stores_minima_then_next_round_resets_everyone_without_rolling_and_keeps_old_round()
    {
        var store = Store(); var die = new SequenceDie(1, 6, 6);
        await Run(store, die); var tied = Round(store);
        Assert.Equal("tie", Game(store).Status); Assert.Equal(new[] { "p001", "p002" }, tied.TiedParticipantIds);
        var next = await Run(store, die, next: true, etag: Etag(5));
        Assert.Equal(201, next.Command.ResponseStatus); Assert.Equal($"/api/v1/games/{GameFixture.Id:D}", next.Command.ResponseLocation);
        Assert.Equal(tied, Round(store)); Assert.Equal(1, die.Calls); Assert.Equal(2, Game(store).CurrentRoundNumber);
        Assert.Equal("rolling", Game(store).Status); Assert.All(Round(store, 2).Rolls, r => { Assert.Null(r.Value); Assert.Null(r.RolledAt); });
        Assert.Null(store.LastBatch![0].Etag); Assert.NotNull(store.LastBatch[1].Etag);
        await Run(store, die, request: new(2, "remaining"), etag: Etag(6));
        Assert.Equal("tie", Game(store).Status); Assert.Equal(tied, Round(store));
        Assert.True(GameReadValidation.Snapshot(Game(store), Round(store, 2), DataTests.Owner));
    }

    [Fact]
    public async Task Mixed_individual_and_remaining_race_preserves_the_winning_values_and_stale_round_requests_fail()
    {
        var store = Store(); var die = new SequenceDie(3, 6);
        store.BeforeWrite = s => { s.BeforeWrite = null; Run(s, die).GetAwaiter().GetResult(); };
        Assert.Equal(412, (await Assert.ThrowsAsync<ApiException>(() => Run(store, die, request: new(1, "individual", "p002")))).StatusCode);
        Assert.Equal(6, Round(store).Rolls[1].Value); Assert.Equal(1, Round(store).Rolls[0].Value);
        store = Store("tie"); await Run(store, die, next: true);
        Assert.Equal("ROUND_MISMATCH", (await Assert.ThrowsAsync<ApiException>(() => Run(store, die, etag: Etag(5)))).Code);
        Assert.Equal("INVALID_GAME_STATE", (await Assert.ThrowsAsync<ApiException>(() => Run(store, die, next: true, etag: Etag(5)))).Code);
    }

    [Fact]
    public async Task A_failed_batch_without_commit_does_not_save_values_or_command_and_the_key_can_be_retried()
    {
        var store = Store(); var key = Guid.NewGuid();
        store.BeforeWrite = _ => throw CosmosErrors.Unavailable();
        Assert.Equal(503, (await Assert.ThrowsAsync<ApiException>(() => Run(store, new SequenceDie(6), key))).StatusCode);
        Assert.Null(Round(store).Rolls[1].Value); Assert.Equal(4, Game(store).Revision);
        Assert.DoesNotContain(store.Documents.Values, d => d.Document is CommandDocument);
        store.BeforeWrite = null; await Run(store, new SequenceDie(3), key);
        Assert.Equal(3, Round(store).Rolls[1].Value); Assert.Equal(5, Game(store).Revision);
    }

    [Theory]
    [InlineData("rolling", false, 1, "individual", "p001", "ALREADY_ROLLED")]
    [InlineData("rolling", false, 1, "individual", "outside", "VALIDATION_ERROR")]
    [InlineData("rolling", false, 2, "remaining", null, "ROUND_MISMATCH")]
    [InlineData("tie", true, 2, "next", null, "ROUND_MISMATCH")]
    [InlineData("rolling", true, 1, "next", null, "INVALID_GAME_STATE")]
    [InlineData("tie", false, 1, "remaining", null, "INVALID_GAME_STATE")]
    [InlineData("awaitingPayment", false, 1, "remaining", null, "INVALID_GAME_STATE")]
    [InlineData("awaitingPayment", true, 1, "next", null, "INVALID_GAME_STATE")]
    [InlineData("completed", false, 1, "remaining", null, "INVALID_GAME_STATE")]
    [InlineData("cancelled", true, 1, "next", null, "INVALID_GAME_STATE")]
    [InlineData("deleted", false, 1, "remaining", null, "GAME_DELETED")]
    public async Task Invalid_operations_do_not_roll_or_write(string status, bool next, int round, string mode, string? person, string code)
    {
        var store = Store(status); var die = new SequenceDie();
        var error = await Assert.ThrowsAsync<ApiException>(() => Run(store, die, request: new(round, mode, person), next: next));
        Assert.Equal(code, error.Code); Assert.Equal(0, die.Calls); Assert.Equal(0, store.Writes);
    }

    [Fact]
    public async Task Missing_foreign_stale_deleted_and_corrupt_data_follow_error_order()
    {
        foreach (var fault in new[] { "missing", "foreign", "stale", "deleted", "round" })
        {
            var store = Store(fault == "deleted" ? "deleted" : "rolling"); var game = Game(store);
            if (fault == "missing") store.Documents.Remove(game.Id);
            if (fault == "foreign") store.Put(game with { OwnerKey = "other" }, game.Id);
            if (fault == "round") store.Documents.Remove(Round(store).Id);
            var error = await Assert.ThrowsAsync<ApiException>(() => Run(store, new SequenceDie(), etag: fault is "stale" or "deleted" ? Etag(1) : Etag()));
            Assert.Equal(fault is "missing" or "foreign" ? 404 : fault == "stale" ? 412 : fault == "deleted" ? 410 : 503, error.StatusCode);
            Assert.Equal(0, store.Writes);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replay_keeps_original_response_even_without_game_and_changed_if_match_reuses_key(bool next)
    {
        var store = Store(next ? "tie" : "rolling"); var key = Guid.NewGuid(); var die = new SequenceDie(6);
        var original = await Run(store, die, key, next: next);
        store.Documents.Clear(); store.Put(original.Command, original.Command.Id);
        var replay = await Run(store, die, key, next: next);
        Assert.True(replay.Replayed); Assert.Equal(original.Command, replay.Command); Assert.Equal(1, store.Writes);
        var error = await Assert.ThrowsAsync<ApiException>(() => Run(store, die, key, next: next, etag: Etag(5)));
        Assert.Equal("IDEMPOTENCY_KEY_REUSED", error.Code); Assert.Equal(next ? 0 : 1, die.Calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Competing_commands_commit_once_and_same_key_replays_different_key_gets_412(bool next, bool same)
    {
        var store = Store(next ? "tie" : "rolling"); var key = Guid.NewGuid(); var die = new SequenceDie(5, 6);
        store.BeforeWrite = s => { s.BeforeWrite = null; Run(s, die, same ? key : Guid.NewGuid(), next: next).GetAwaiter().GetResult(); };
        if (same) Assert.True((await Run(store, die, key, next: next)).Replayed);
        else Assert.Equal(412, (await Assert.ThrowsAsync<ApiException>(() => Run(store, die, key, next: next))).StatusCode);
        Assert.Equal(5, Game(store).Revision); Assert.Single(store.Documents.Values, d => d.Document is CommandDocument);
        Assert.Equal(next ? 2 : 1, store.Documents.Values.Count(d => d.Document is RoundDocument));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lost_commit_response_reconciles_command_without_another_write(bool next)
    {
        var store = Store(next ? "tie" : "rolling"); store.LoseResponse = true; var die = new SequenceDie(6);
        var result = await Run(store, die, next: next);
        Assert.True(result.Replayed); Assert.Equal(1, store.Writes); Assert.Equal(5, Game(store).Revision);
    }

    [Fact]
    public async Task Same_key_from_creation_is_rejected_and_invalid_random_value_is_never_saved()
    {
        var store = new CreateStore(); var key = Guid.NewGuid(); await GameCreateTests.Create(store, key);
        Assert.Equal("IDEMPOTENCY_KEY_REUSED", (await Assert.ThrowsAsync<ApiException>(() => Run(store, new SequenceDie(), key))).Code);
        store = Store();
        Assert.Equal(503, (await Assert.ThrowsAsync<ApiException>(() => Run(store, new SequenceDie(0)))).StatusCode);
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public void Production_die_always_returns_a_valid_face()
    { var die = new SecureDie(); for (var i = 0; i < 1000; i++) Assert.InRange(die.Roll(), 1, 6); }

    [Theory]
    [InlineData(1, 3, 3, "p001")]
    [InlineData(6, 5, 1, "p003")]
    [InlineData(1, 1, 6, null)]
    [InlineData(6, 1, 1, null)]
    public async Task Only_a_tied_minimum_causes_a_tie_and_next_round_includes_non_tied_people(int a, int b, int c, string? loser)
    {
        var store = Store(); var game = Game(store);
        var people = new[] { "p001", "p002", "p003" };
        store.Put(game with { Participants = people.Select((p, i) => new GameParticipant { ParticipantId = p, DisplayName = p, Order = i + 1 }).ToArray() }, game.Id);
        store.Put(Round(store) with { Rolls = people.Select(p => new RollSnapshot { ParticipantId = p, Value = null, RolledAt = null }).ToArray() }, Round(store).Id);
        var die = new SequenceDie(a, b, c); await Run(store, die);
        Assert.Equal(loser, Game(store).LoserParticipantId); Assert.Equal(loser is null ? "tie" : "awaitingPayment", Game(store).Status);
        Assert.True(GameReadValidation.Snapshot(Game(store), Round(store), DataTests.Owner));
        if (loser is null)
        {
            await Run(store, die, next: true, etag: Etag(5));
            Assert.Equal(people, Round(store, 2).Rolls.Select(r => r.ParticipantId)); Assert.All(Round(store, 2).Rolls, r => Assert.Null(r.Value));
            Assert.Equal(3, die.Calls);
        }
    }

    [Fact]
    public async Task Repeated_round_condition_conflicts_are_bounded_and_cancellation_does_no_IO()
    {
        var store = Store(); store.BeforeWrite = s => s.Put(Round(s), Round(s).Id);
        Assert.Equal(503, (await Assert.ThrowsAsync<ApiException>(() => Run(store, new SequenceDie(6, 6, 6)))).StatusCode);
        Assert.Equal(3, store.Writes); Assert.Equal(4, Game(store).Revision); Assert.Null(Round(store).Rolls[1].Value);
        Assert.DoesNotContain(store.Documents.Values, d => d.Document is CommandDocument);
        store = Store(); using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GameDice(store, store, new SequenceDie()).ExecuteAsync(DataTests.Owner,
            GameFixture.Id, Guid.NewGuid(), "hash", Etag(), new(1, "remaining"), false, GameReaderTests.Session(), canceled.Token));
        Assert.Equal(0, store.Reads); Assert.Equal(0, store.Writes);
    }

    internal IHost Server(CreateStore store, IDie die) => new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(s => s.AddMvc())
        .Configure(app => app.Run(context => new ApiHttpPipeline(tokens.Validator(), NullLogger<ApiHttpPipeline>.Instance).InvokeAsync(context, false, async () =>
        {
            var functions = new GameDiceFunctions(new GameDice(store, store, die));
            var result = context.Request.Path.Value!.EndsWith("/rounds") ? await functions.NextRound(context.Request, GameFixture.Id.ToString("D")) :
                await functions.Roll(context.Request, GameFixture.Id.ToString("D"));
            await result.ExecuteResultAsync(new ActionContext(context, new RouteData(), new ActionDescriptor()));
        })))).Start();

    [Theory]
    [InlineData("{\"roundNumber\":1,\"mode\":\"remaining\",\"participantId\":null}", false, 422)]
    [InlineData("{\"roundNumber\":1,\"mode\":\"individual\"}", false, 422)]
    [InlineData("{\"roundNumber\":1,\"mode\":\"unknown\"}", false, 422)]
    [InlineData("{\"roundNumber\":1,\"mode\":\"remaining\",\"value\":6}", false, 422)]
    [InlineData("{\"roundNumber\":1,\"roundNumber\":1,\"mode\":\"remaining\"}", false, 422)]
    [InlineData("{\"roundNumber\":0,\"mode\":\"remaining\"}", false, 422)]
    [InlineData("{\"roundNumber\":1.0,\"mode\":\"remaining\"}", false, 422)]
    [InlineData("{\"roundNumber\":1e0,\"mode\":\"remaining\"}", false, 422)]
    [InlineData("{\"previousRoundNumber\":1,\"extra\":true}", true, 422)]
    [InlineData("{\"previousRoundNumber\":-1}", true, 422)]
    [InlineData("{\"previousRoundNumber\":\"1\"}", true, 422)]
    [InlineData("[]", true, 422)]
    [InlineData("{", false, 400)]
    public async Task Invalid_bodies_rejected_before_DB(string body, bool next, int status)
    {
        var store = Store(); using var server = Server(store, new SequenceDie()); using var client = server.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.Token());
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString()); client.DefaultRequestHeaders.Add("If-Match", Etag());
        var response = await client.PostAsync($"/api/v1/games/{GameFixture.Id:D}/{(next ? "rounds" : "rolls")}", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(status, (int)response.StatusCode); Assert.Equal(0, store.Reads); Assert.Equal(0, store.Writes);
    }

    [Theory]
    [InlineData("missing-auth", 401)]
    [InlineData("forbidden", 403)]
    [InlineData("missing-key", 428)]
    [InlineData("missing-etag", 428)]
    [InlineData("weak", 400)]
    [InlineData("wildcard", 400)]
    [InlineData("multiple", 400)]
    [InlineData("overflow", 400)]
    [InlineData("media", 415)]
    [InlineData("oversize", 413)]
    [InlineData("query", 400)]
    [InlineData("session", 400)]
    public async Task Auth_headers_limits_and_query_rejected_before_DB(string fault, int status)
    {
        var store = Store(); using var server = Server(store, new SequenceDie()); using var client = server.GetTestClient();
        if (fault != "missing-auth") client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.Token(d => { if (fault == "forbidden") d.Claims["oid"] = Guid.NewGuid().ToString(); }));
        if (fault != "missing-key") client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        if (fault != "missing-etag") client.DefaultRequestHeaders.TryAddWithoutValidation("If-Match", fault switch
            { "weak" => "W/" + Etag(), "wildcard" => "*", "multiple" => Etag() + "," + Etag(), "overflow" => Etag().Replace("v4", "v999999999999999999999999"), _ => Etag() });
        if (fault == "session") client.DefaultRequestHeaders.Add("X-Read-Session", "bad=");
        var response = await client.PostAsync($"/api/v1/games/{GameFixture.Id:D}/rolls" + (fault == "query" ? "?x=1" : ""),
            new StringContent(fault == "oversize" ? new string(' ', 16385) : "{\"roundNumber\":1,\"mode\":\"remaining\"}", Encoding.UTF8, fault == "media" ? "text/plain" : "application/json"));
        Assert.Equal(status, (int)response.StatusCode); Assert.Equal(0, store.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HTTP_success_and_replay_publish_body_etag_location_session_and_no_store(bool next)
    {
        var store = Store(next ? "tie" : "rolling"); var die = new SequenceDie(6);
        using var server = Server(store, die); using var client = server.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.Token());
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString()); client.DefaultRequestHeaders.Add("If-Match", Etag());
        var path = $"/api/v1/games/{GameFixture.Id:D}/{(next ? "rounds" : "rolls")}";
        var body = next ? "{\"previousRoundNumber\":1}" : "{\"roundNumber\":1,\"mode\":\"remaining\"}";
        var first = await client.PostAsync(path, new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(next ? 201 : 200, (int)first.StatusCode); Assert.Equal(Etag(5), first.Headers.ETag!.ToString());
        Assert.Equal(next ? $"/api/v1/games/{GameFixture.Id:D}" : null, first.Headers.Location?.ToString()); Assert.True(first.Headers.CacheControl!.NoStore);
        Assert.Equal("false", first.Headers.GetValues("Idempotency-Replayed").Single());
        var replay = await client.PostAsync(path, new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(first.StatusCode, replay.StatusCode); Assert.Equal(await first.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single());
        Assert.StartsWith("0:-1#", ReadSession.Decode(replay.Headers.GetValues("X-Read-Session").Single()));
        Assert.DoesNotContain("ownerKey", await replay.Content.ReadAsStringAsync()); Assert.Equal(1, store.Writes);
    }
}
