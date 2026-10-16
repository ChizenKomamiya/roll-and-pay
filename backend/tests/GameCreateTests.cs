using System.Net;
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

// Models conditional writes for deterministic failure/race injection; real Cosmos atomicity has separate emulator tests.
internal sealed class CreateStore : ICosmosPointReader, ICosmosBatchWriter
{
    internal readonly Dictionary<string, (object Document, string Etag)> Documents = [];
    internal int Reads, Writes;
    internal Action<CreateStore>? BeforeWrite;
    internal bool LoseResponse;
    internal IReadOnlyList<BatchWrite>? LastBatch;
    private readonly object gate = new();
    private int version = 1;
    internal CreateStore()
    {
        Put(DataTests.Roster(DataTests.Person(), DataTests.Person("p002", "名前B", 0), DataTests.Person("inactive", active: false)), "roster");
        Put(GameFixture.Control(), "control");
    }
    internal void Put(object value, string id) => Documents[id] = (value, $"etag-{++version}");
    public Task<DocumentRead<T>> ReadAsync<T>(string owner, string id, string? session, CancellationToken token) where T : class
    {
        token.ThrowIfCancellationRequested();
        Assert.Equal(DataTests.Owner, owner);
        lock (gate)
        {
            Reads++;
            var found = Documents.TryGetValue(id, out var item);
            return Task.FromResult(new DocumentRead<T>(found ? (T)item.Document : null, $"0:-1#{version}", found ? item.Etag : null));
        }
    }
    public Task<BatchResult> WriteAsync(string owner, IReadOnlyList<BatchWrite> writes, string? session, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Assert.Equal(DataTests.Owner, owner);
        lock (gate)
        {
            Writes++; LastBatch = writes;
            BeforeWrite?.Invoke(this);
            foreach (var write in writes)
            {
                var exists = Documents.TryGetValue(write.Id, out var item);
                if (write.Etag is null ? exists : !exists || item.Etag != write.Etag)
                    return Task.FromResult(new BatchResult(true, $"0:-1#{version}"));
            }
            foreach (var write in writes) Put(write.Document, write.Id);
            if (LoseResponse) throw CosmosErrors.Unavailable();
            return Task.FromResult(new BatchResult(false, $"0:-1#{version}"));
        }
    }
    internal GameCreator Creator() => new(this, this, new GameReader(this));
}

public sealed class GameCreateTests(TokenFixture tokens) : IClassFixture<TokenFixture>
{
    internal const string Body = "{\"participantIds\":[\"p002\",\"p001\"]}";
    internal static string Hash(string body = Body) { using var json = JsonDocument.Parse(body); return UpdateRequest.Hash("POST", "/api/v1/games", json.RootElement, null); }
    internal static Task<CommandResponse> Create(CreateStore store, Guid key, string[]? ids = null, string? hash = null) =>
        store.Creator().CreateAsync(DataTests.Owner, key, hash ?? Hash(), ids ?? ["p002", "p001"], GameReaderTests.Session(), default);

    [Fact]
    public async Task Creates_five_documents_atomically_preserves_selection_names_and_replays_without_current_state()
    {
        var store = new CreateStore(); var key = Guid.NewGuid();
        var created = await Create(store, key);
        Assert.False(created.Replayed); Assert.Equal(201, created.Command.ResponseStatus);
        Assert.Equal(5, store.LastBatch!.Count);
        Assert.Equal(new[] { false, false, true, true, false }, store.LastBatch.Select(w => w.Etag is not null));
        var game = await new GameReader(store).GetActiveAsync(DataTests.Owner, GameReaderTests.Session(), default);
        Assert.Equal(1, game!.Revision); Assert.Equal("rolling", game.Status);
        Assert.Equal(new[] { "p002", "p001" }, game.Participants.Select(p => p.ParticipantId));
        Assert.Equal(new[] { 1, 2 }, game.Participants.Select(p => p.Order));
        Assert.Equal("名前B", game.Participants[0].DisplayName);
        Assert.All(game.CurrentRound.Rolls, r => { Assert.Null(r.Value); Assert.Null(r.RolledAt); });
        Assert.Equal(game.CreatedAt, game.CurrentRound.CreatedAt);
        store.Documents.Clear(); store.Put(created.Command, created.Command.Id);
        var replay = await Create(store, key);
        Assert.True(replay.Replayed); Assert.Equal(created.Command, replay.Command); Assert.Equal(1, store.Writes);
    }

    [Fact]
    public async Task Different_body_or_operation_using_the_same_key_is_rejected_before_roster_or_state_reads()
    {
        var store = new CreateStore(); var key = Guid.NewGuid(); var saved = await Create(store, key);
        store.Documents.Clear(); store.Put(saved.Command, saved.Command.Id);
        var before = store.Reads;
        var e = await Assert.ThrowsAsync<ApiException>(() => Create(store, key, ["p001", "p002"], Hash("{\"participantIds\":[\"p001\",\"p002\"]}")));
        Assert.Equal("IDEMPOTENCY_KEY_REUSED", e.Code); Assert.Equal(before + 1, store.Reads);
        Assert.NotEqual(Hash(), UpdateRequest.Hash("DELETE", "/api/v1/games", null, null));
    }

    [Fact]
    public async Task Commit_followed_by_lost_response_is_reconciled_from_command_and_does_not_create_twice()
    {
        var store = new CreateStore { LoseResponse = true }; var key = Guid.NewGuid();
        var response = await Create(store, key);
        Assert.True(response.Replayed); Assert.Equal(1, store.Writes);
        Assert.True((await Create(store, key)).Replayed);
        Assert.Single(store.Documents.Values, d => d.Document is GameDocument);
    }

    [Fact]
    public async Task Competing_different_key_creates_only_one_active_game_and_returns_its_id()
    {
        var store = new CreateStore(); CommandResponse? winner = null;
        store.BeforeWrite = s => { s.BeforeWrite = null; winner = Create(s, Guid.NewGuid()).GetAwaiter().GetResult(); };
        var error = await Assert.ThrowsAsync<ApiException>(() => Create(store, Guid.NewGuid()));
        Assert.Equal("ACTIVE_GAME_EXISTS", error.Code);
        Assert.Equal(winner!.Command.GameId.ToString("D"), Assert.Single(error.Details).GameId);
        Assert.Single(store.Documents.Values, d => d.Document is GameDocument);
    }

    [Fact]
    public async Task Competing_same_key_replays_winner_instead_of_reporting_active_game()
    {
        var store = new CreateStore(); var key = Guid.NewGuid();
        store.BeforeWrite = s => { s.BeforeWrite = null; Create(s, key).GetAwaiter().GetResult(); };
        var result = await Create(store, key);
        Assert.True(result.Replayed); Assert.Single(store.Documents.Values, d => d.Document is GameDocument);
    }

    [Fact]
    public async Task Roster_change_between_validation_and_commit_rolls_back_then_revalidates()
    {
        var store = new CreateStore();
        store.BeforeWrite = s => { s.BeforeWrite = null; s.Put(DataTests.Roster(DataTests.Person(), DataTests.Person("p002", active: false)), "roster"); };
        var error = await Assert.ThrowsAsync<ApiException>(() => Create(store, Guid.NewGuid()));
        Assert.Equal(422, error.StatusCode); Assert.Equal(2, store.Documents.Count);
        Assert.Null(((ControlDocument)store.Documents["control"].Document).ActiveGameId);
    }

    [Fact]
    public async Task Continuing_conditional_conflicts_are_bounded_and_leave_no_partial_writes()
    {
        var store = new CreateStore();
        store.BeforeWrite = s => s.Put(s.Documents["roster"].Document, "roster");
        var error = await Assert.ThrowsAsync<ApiException>(() => Create(store, Guid.NewGuid()));
        Assert.Equal(503, error.StatusCode); Assert.Equal(3, store.Writes); Assert.Equal(2, store.Documents.Count);
    }

    [Theory]
    [InlineData("inactive")]
    [InlineData("unknown")]
    public async Task Unknown_or_inactive_roster_participants_are_422_without_writes(string id)
    {
        var store = new CreateStore();
        var error = await Assert.ThrowsAsync<ApiException>(() => Create(store, Guid.NewGuid(), ["p001", id]));
        Assert.Equal(422, error.StatusCode); Assert.Equal(0, store.Writes);
    }

    [Fact]
    public async Task Maximum_roster_and_selection_fit_well_below_batch_size_limit()
    {
        var store = new CreateStore();
        var people = Enumerable.Range(0, 100).Select(i => DataTests.Person(i.ToString().PadLeft(40, 'p'), new string('名', 50))).ToArray();
        store.Put(DataTests.Roster(people), "roster");
        await Create(store, Guid.NewGuid(), people.Take(6).Select(p => p.ParticipantId).ToArray());
        var options = CosmosConnection.ClientOptions().UseSystemTextJsonSerializerWithOptions;
        var bytes = store.LastBatch!.Sum(w => JsonSerializer.SerializeToUtf8Bytes(w.Document, options).Length);
        Assert.InRange(bytes, 1, 128 * 1024); // Payload documents only, leaving ample room for the five operation envelopes.
        Assert.Equal(5, store.LastBatch!.Count);
    }

    [Fact]
    public async Task Cancellation_is_propagated_before_DB_access()
    {
        var store = new CreateStore(); using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.Creator().CreateAsync(DataTests.Owner, Guid.NewGuid(), Hash(),
            ["p002", "p001"], GameReaderTests.Session(), canceled.Token));
        Assert.Equal(0, store.Reads); Assert.Equal(0, store.Writes);
    }

    [Theory]
    [InlineData("roster")]
    [InlineData("control")]
    [InlineData("pointer")]
    public async Task Missing_initial_data_and_broken_pointer_stop_creation(string fault)
    {
        var store = new CreateStore();
        if (fault == "pointer") store.Put(GameFixture.Control(GameFixture.Id), "control"); else store.Documents.Remove(fault);
        var error = await Assert.ThrowsAsync<ApiException>(() => Create(store, Guid.NewGuid()));
        Assert.Equal(503, error.StatusCode); Assert.Equal(0, store.Writes);
    }

    internal IHost Server(CreateStore store) => new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(s => s.AddMvc())
        .Configure(app => app.Run(context => new ApiHttpPipeline(tokens.Validator(), NullLogger<ApiHttpPipeline>.Instance).InvokeAsync(context, false, async () =>
        {
            var result = await new GameCreateFunction(store.Creator()).Run(context.Request);
            await result.ExecuteResultAsync(new ActionContext(context, new RouteData(), new ActionDescriptor()));
        })))).Start();

    [Theory]
    [InlineData("{\"participantIds\":[\"p001\"]}", 422)]
    [InlineData("{\"participantIds\":[]}", 422)]
    [InlineData("{\"participantIds\":[\"p001\",\"p002\",\"p003\",\"p004\",\"p005\",\"p006\",\"p007\"]}", 422)]
    [InlineData("{\"participantIds\":[\"p001\",\"p001\"]}", 422)]
    [InlineData("{\"participantIds\":[\"p001\",null]}", 422)]
    [InlineData("{\"participantIds\":[1,2]}", 422)]
    [InlineData("{\"participantIds\":[\"a/b\",\"p001\"]}", 422)]
    [InlineData("{\"participantIds\":null}", 422)]
    [InlineData("{\"participantIds\":[\"p001\",\"p002\"],\"ownerKey\":\"x\"}", 422)]
    [InlineData("{\"participantIds\":[],\"participantIds\":[\"p001\",\"p002\"]}", 422)]
    [InlineData("[]", 422)]
    [InlineData("{}", 422)]
    [InlineData("{", 400)]
    [InlineData("", 400)]
    public async Task Body_validation_happens_before_any_DB_access(string body, int status)
    {
        var store = new CreateStore(); using var server = Server(store); using var client = server.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.Token());
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await client.PostAsync("/api/v1/games", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(status, (int)response.StatusCode); Assert.Equal(0, store.Reads); Assert.Equal(0, store.Writes);
    }

    [Theory]
    [InlineData("missing-auth", 401)]
    [InlineData("forbidden", 403)]
    [InlineData("missing-key", 428)]
    [InlineData("bad-key", 400)]
    [InlineData("duplicate-key", 400)]
    [InlineData("query", 400)]
    [InlineData("if-match", 400)]
    [InlineData("session", 400)]
    [InlineData("media", 415)]
    [InlineData("oversize", 413)]
    public async Task Authentication_headers_and_body_limits_reject_before_DB(string fault, int status)
    {
        var store = new CreateStore(); using var server = Server(store); using var client = server.GetTestClient();
        if (fault != "missing-auth") client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.Token(d =>
            { if (fault == "forbidden") d.Claims["oid"] = Guid.NewGuid().ToString(); }));
        if (fault != "missing-key") client.DefaultRequestHeaders.Add("Idempotency-Key", fault == "bad-key" ? "bad" : Guid.NewGuid().ToString());
        if (fault == "duplicate-key") client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        if (fault == "if-match") client.DefaultRequestHeaders.Add("If-Match", "*");
        if (fault == "session") client.DefaultRequestHeaders.Add("X-Read-Session", "bad=");
        var response = await client.PostAsync("/api/v1/games" + (fault == "query" ? "?x=1" : ""),
            new StringContent(fault == "oversize" ? new string(' ', 16385) : Body, Encoding.UTF8, fault == "media" ? "text/plain" : "application/json"));
        Assert.Equal(status, (int)response.StatusCode); Assert.Equal(0, store.Reads); Assert.Equal(0, store.Writes);
    }

    [Fact]
    public async Task HTTP_creation_and_replay_keep_the_same_body_location_etag_and_publish_latest_session()
    {
        var store = new CreateStore(); using var server = Server(store); using var client = server.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.Token());
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString().ToUpperInvariant());
        var first = await client.PostAsync("/api/v1/games", new StringContent(Body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode); Assert.True(first.Headers.CacheControl!.NoStore);
        Assert.Equal("false", first.Headers.GetValues("Idempotency-Replayed").Single());
        var text = await first.Content.ReadAsStringAsync(); Assert.DoesNotContain("ownerKey", text); Assert.DoesNotContain("etag-", text);
        var replay = await client.PostAsync("/api/v1/games", new StringContent(" { \"participantIds\" : [ \"p002\", \"p001\" ] } ", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode); Assert.Equal(text, await replay.Content.ReadAsStringAsync());
        Assert.Equal(first.Headers.ETag, replay.Headers.ETag); Assert.Equal(first.Headers.Location, replay.Headers.Location);
        Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single());
        Assert.StartsWith("0:-1#", ReadSession.Decode(replay.Headers.GetValues("X-Read-Session").Single()));
        var conflictClient = server.GetTestClient(); conflictClient.DefaultRequestHeaders.Authorization = new("Bearer", tokens.Token());
        conflictClient.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var conflict = await conflictClient.PostAsync("/api/v1/games", new StringContent(Body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode); Assert.Null(conflict.Headers.ETag);
        using var json = JsonDocument.Parse(await conflict.Content.ReadAsStringAsync());
        Assert.Equal("ACTIVE_GAME_EXISTS", json.RootElement.GetProperty("error").GetProperty("details")[0].GetProperty("reason").GetString());
    }

    [Fact]
    public void Hash_ignores_object_order_but_preserves_array_order_method_path_if_match_and_absent_body()
    {
        using var a = JsonDocument.Parse("{\"b\":2,\"a\":[1,2]}"); using var b = JsonDocument.Parse(" { \"a\" : [1,2], \"b\":2 }");
        Assert.Equal(UpdateRequest.Hash("post", "/x", a.RootElement, "etag"), UpdateRequest.Hash("POST", "/x", b.RootElement, "etag"));
        Assert.NotEqual(Hash(), Hash("{\"participantIds\":[\"p001\",\"p002\"]}"));
        Assert.NotEqual(UpdateRequest.Hash("POST", "/x", a.RootElement, null), UpdateRequest.Hash("POST", "/x", a.RootElement, "etag"));
        using var empty = JsonDocument.Parse("{}");
        Assert.NotEqual(UpdateRequest.Hash("DELETE", "/x", null, null), UpdateRequest.Hash("DELETE", "/x", empty.RootElement, null));
    }
}

