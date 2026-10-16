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
using Moq;
using RollAndPay.Api.Data;
using RollAndPay.Api.Functions;
using RollAndPay.Api.Games;
using RollAndPay.Api.Http;
using RollAndPay.Api.Payments;
using Xunit;

namespace RollAndPay.Api.Tests;

public sealed class PaymentPreviewHttpTests(TokenFixture tokens) : IClassFixture<TokenFixture>
{
    private IHost Server(ICosmosPointReader store)
    {
        var pipeline = new ApiHttpPipeline(tokens.Validator(), NullLogger<ApiHttpPipeline>.Instance);
        var function = new PaymentPreviewFunction(new GameReader(store));
        return new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(s => s.AddMvc())
            .Configure(app => app.Run(context => pipeline.InvokeAsync(context, false, async () =>
            {
                var result = await function.Run(context.Request, context.Request.Path.Value!.Split('/')[4]);
                await result.ExecuteResultAsync(new ActionContext(context, new RouteData(), new ActionDescriptor()));
            })))).Start();
    }

    private static Mock<ICosmosPointReader> ReadyStore(int count = 4, int loserIndex = 0)
    {
        var participants = PaymentCalculatorTests.Participants(count);
        return GameReaderTests.Store(GameFixture.Game("awaitingPayment") with
        {
            Participants = participants, LoserParticipantId = participants[loserIndex].ParticipantId
        }, GameFixture.Round("awaitingPayment") with
        {
            Rolls = participants.Select((p, i) => new RollSnapshot { ParticipantId = p.ParticipantId,
                Value = i == loserIndex ? 1 : 6, RolledAt = GameFixture.Now.AddSeconds(30) }).ToArray()
        });
    }

    private HttpClient Client(IHost server)
    {
        var client = server.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.Token());
        return client;
    }

    private static string Path(string? id = null) => $"/api/v1/games/{id ?? GameFixture.Id.ToString().ToUpperInvariant()}/payment-preview";

    [Theory]
    [InlineData(13800, 4, 0, 3000, 1800)]
    [InlineData(4000, 4, 2, 1000, 0)]
    [InlineData(3999, 4, 3, 0, 3999)]
    [InlineData(1, 2, 1, 0, 1)]
    [InlineData(9999999, 6, 5, 1666000, 3999)]
    public async Task Preview_uses_saved_participants_and_loser_returns_ETag_and_never_writes(
        long total, int count, int loserIndex, long baseYen, long loss)
    {
        var store = ReadyStore(count, loserIndex);
        using var server = Server(store.Object);
        using var client = Client(server);
        client.DefaultRequestHeaders.Add("X-Owner-Key", "untrusted");
        // A08 requires neither an idempotency key nor If-Match.
        var response = await client.PostAsync(Path(), new StringContent($"{{\"totalYen\":{total}}}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(GameEtag.Create(GameFixture.Id, 4), response.Headers.ETag!.ToString());
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal("0:-1#2", ReadSession.Decode(response.Headers.GetValues(ReadSession.HeaderName).Single()));
        Assert.Contains("ETag", string.Join(",", response.Headers.GetValues("Access-Control-Expose-Headers")));
        var text = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(text);
        Assert.Equal(3, json.RootElement.EnumerateObject().Count());
        Assert.Equal(GameFixture.Id, json.RootElement.GetProperty("gameId").GetGuid());
        Assert.Equal(4, json.RootElement.GetProperty("revision").GetInt64());
        var payment = json.RootElement.GetProperty("payment");
        Assert.Equal(10, payment.EnumerateObject().Count());
        Assert.Equal(total, payment.GetProperty("totalYen").GetInt64());
        Assert.Equal(count, payment.GetProperty("participantCount").GetInt32());
        Assert.Equal(baseYen, payment.GetProperty("baseYen").GetInt64());
        Assert.Equal(loss, payment.GetProperty("lossYen").GetInt64());
        Assert.Equal(baseYen + loss, payment.GetProperty("loserPaymentYen").GetInt64());
        Assert.Equal(baseYen * (count - 1), payment.GetProperty("collectFromOthersYen").GetInt64());
        Assert.Equal(PaymentCalculatorTests.Participants(count)[loserIndex].ParticipantId, payment.GetProperty("loserParticipantId").GetString());
        Assert.Equal(count, payment.GetProperty("shares").GetArrayLength());
        Assert.Equal(total, payment.GetProperty("shares").EnumerateArray().Sum(s => s.GetProperty("amountYen").GetInt64()));
        Assert.Equal(JsonValueKind.Null, payment.GetProperty("registeredAt").ValueKind);
        Assert.Equal("thousand-remainder-v1", payment.GetProperty("calculationVersion").GetString());
        Assert.DoesNotContain("ownerKey", text);
        Assert.DoesNotContain("internal-game-etag", text);
        // Only two game reads and one round read; no roster, control, command or writer access.
        Assert.Equal(3, store.Invocations.Count);
        store.Verify(s => s.ReadAsync<GameDocument>(DataTests.Owner, $"game:{GameFixture.Id:D}", It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        store.Verify(s => s.ReadAsync<RoundDocument>(DataTests.Owner, $"round:{GameFixture.Id:D}:1", It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        // Repeating a preview with a different amount leaves the same revision and saved payment.
        var second = await client.PostAsync(Path(), new StringContent("{\"totalYen\":123}", Encoding.UTF8, "application/json"));
        Assert.Equal(response.Headers.ETag, second.Headers.ETag);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(6, store.Invocations.Count);
    }

    [Theory]
    [InlineData("{}", 422)]
    [InlineData("null", 422)]
    [InlineData("[]", 422)]
    [InlineData("{\"TotalYen\":1}", 422)]
    [InlineData("{\"totalYen\":0}", 422)]
    [InlineData("{\"totalYen\":-1}", 422)]
    [InlineData("{\"totalYen\":10000000}", 422)]
    [InlineData("{\"totalYen\":999999999999999999999999999999}", 422)]
    [InlineData("{\"totalYen\":13800.0}", 422)]
    [InlineData("{\"totalYen\":1.5}", 422)]
    [InlineData("{\"totalYen\":1e3}", 422)]
    [InlineData("{\"totalYen\":1E+3}", 422)]
    [InlineData("{\"totalYen\":\"13800\"}", 422)]
    [InlineData("{\"totalYen\":null}", 422)]
    [InlineData("{\"totalYen\":true}", 422)]
    [InlineData("{\"totalYen\":{}}", 422)]
    [InlineData("{\"totalYen\":1,\"lossYen\":1}", 422)]
    [InlineData("{\"totalYen\":1,\"totalYen\":2}", 422)]
    [InlineData("", 400)]
    [InlineData("{", 400)]
    [InlineData("{\"totalYen\":01}", 400)]
    [InlineData("{\"totalYen\":1,}", 400)]
    [InlineData("{\"totalYen\":1} {}", 400)]
    [InlineData("{/*comment*/\"totalYen\":1}", 400)]
    public async Task Invalid_JSON_or_values_are_rejected_before_DB(string body, int status)
    {
        var store = new Mock<ICosmosPointReader>(MockBehavior.Strict);
        using var server = Server(store.Object);
        using var client = Client(server);
        var response = await client.PostAsync(Path(), new StringContent(body, Encoding.UTF8, "application/json"));
        await Error(response, status, status == 400 ? "INVALID_REQUEST" : "VALIDATION_ERROR");
        store.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("text/plain")]
    [InlineData("application/problem+json")]
    [InlineData("application/json; charset=utf-16")]
    public async Task Unsupported_media_is_415(string? media)
    {
        var store = new Mock<ICosmosPointReader>(MockBehavior.Strict);
        using var server = Server(store.Object);
        using var client = Client(server);
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes("{\"totalYen\":1}"));
        if (media is not null) content.Headers.TryAddWithoutValidation("Content-Type", media);
        await Error(await client.PostAsync(Path(), content), 415, "UNSUPPORTED_MEDIA_TYPE");
        store.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("rolling", 409, "INVALID_GAME_STATE")]
    [InlineData("tie", 409, "INVALID_GAME_STATE")]
    [InlineData("completed", 409, "INVALID_GAME_STATE")]
    [InlineData("cancelled", 409, "INVALID_GAME_STATE")]
    [InlineData("deleted", 410, "GAME_DELETED")]
    [InlineData("missing", 404, "GAME_NOT_FOUND")]
    [InlineData("owner", 404, "GAME_NOT_FOUND")]
    public async Task Only_awaiting_payment_in_the_authenticated_partition_is_accepted(string state, int status, string code)
    {
        var game = state == "missing" ? null : GameFixture.Game(state == "owner" ? "awaitingPayment" : state);
        if (state == "owner") game = game! with { OwnerKey = "other-owner" };
        using var server = Server(GameReaderTests.Store(game, GameFixture.Round(state)).Object);
        using var client = Client(server);
        await Error(await client.PostAsync(Path(), new StringContent("{\"totalYen\":13800}", Encoding.UTF8, "application/json")), status, code);
    }

    [Theory]
    [InlineData("missing-token", 401, "UNAUTHENTICATED")]
    [InlineData("other-user", 403, "FORBIDDEN")]
    [InlineData("id", 400, "INVALID_REQUEST")]
    [InlineData("query", 400, "INVALID_REQUEST")]
    [InlineData("session", 400, "INVALID_READ_SESSION")]
    public async Task Authentication_and_request_format_fail_before_DB(string fault, int status, string code)
    {
        var store = new Mock<ICosmosPointReader>(MockBehavior.Strict);
        using var server = Server(store.Object);
        using var client = Client(server);
        if (fault == "missing-token") client.DefaultRequestHeaders.Authorization = null;
        if (fault == "other-user") client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.Token(d => d.Claims["oid"] = Guid.NewGuid().ToString()));
        if (fault == "session") client.DefaultRequestHeaders.Add(ReadSession.HeaderName, "invalid=");
        var path = fault == "id" ? Path("not-a-uuid") : Path();
        if (fault == "query") path += "?ownerKey=other";
        // Malformed JSON also verifies that authentication precedes body parsing.
        var body = fault is "missing-token" or "other-user" ? "{" : "{\"totalYen\":13800}";
        await Error(await client.PostAsync(path, new StringContent(body, Encoding.UTF8, "application/json")), status, code);
        store.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Body_size_boundary_is_enforced_even_without_ContentLength(bool streaming)
    {
        var store = ReadyStore();
        using var server = Server(store.Object);
        using var client = Client(server);
        foreach (var extra in new[] { 0, 1 })
        {
            var bytes = Encoding.UTF8.GetBytes("{\"totalYen\":1}".PadRight(PaymentRequest.MaxBodyBytes + extra));
            using HttpContent content = streaming ? new UnknownLengthContent(bytes) : new ByteArrayContent(bytes);
            content.Headers.ContentType = new("application/json");
            var response = await client.PostAsync(Path(), content);
            if (extra == 0) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            else await Error(response, 413, "PAYLOAD_TOO_LARGE");
        }
        Assert.Equal(3, store.Invocations.Count);
    }

    [Fact]
    public async Task Incoming_session_is_forwarded_and_DB_failure_has_no_ETag()
    {
        var store = ReadyStore();
        store.Setup(s => s.ReadAsync<GameDocument>(DataTests.Owner, $"game:{GameFixture.Id:D}", "0:-1#1", It.IsAny<CancellationToken>()))
            .ThrowsAsync(CosmosErrors.Unavailable());
        using var server = Server(store.Object);
        using var client = Client(server);
        client.DefaultRequestHeaders.Add(ReadSession.HeaderName, Convert.ToBase64String(Encoding.UTF8.GetBytes("0:-1#1")).TrimEnd('='));
        var response = await client.PostAsync(Path(), new StringContent("{\"totalYen\":13800}", Encoding.UTF8, "application/json"));
        await Error(response, 503, "TEMPORARILY_UNAVAILABLE");
        Assert.Equal(TimeSpan.FromSeconds(5), response.Headers.RetryAfter!.Delta);
        Assert.Single(store.Invocations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_game_update_uses_the_reconfirmed_revision_and_state(bool completed)
    {
        var before = GameFixture.Game("awaitingPayment");
        var after = GameFixture.Game(completed ? "completed" : "awaitingPayment") with { Revision = 5 };
        var store = GameReaderTests.Store(before, GameFixture.Round("awaitingPayment"));
        store.SetupSequence(s => s.ReadAsync<GameDocument>(DataTests.Owner, before.Id, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DocumentRead<GameDocument>(before, "0:-1#2", "old"))
            .ReturnsAsync(new DocumentRead<GameDocument>(after, "0:-1#4", "new"))
            .ReturnsAsync(new DocumentRead<GameDocument>(after, "0:-1#5", "new"))
            .ReturnsAsync(new DocumentRead<GameDocument>(after, "0:-1#6", "new"));
        using var server = Server(store.Object);
        using var client = Client(server);
        var response = await client.PostAsync(Path(), new StringContent("{\"totalYen\":13800}", Encoding.UTF8, "application/json"));
        if (completed) await Error(response, 409, "INVALID_GAME_STATE");
        else
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(GameEtag.Create(GameFixture.Id, 5), response.Headers.ETag!.ToString());
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(5, json.RootElement.GetProperty("revision").GetInt64());
        }
        Assert.Equal(6, store.Invocations.Count);
    }

    [Fact]
    public async Task Successful_preview_forwards_the_session_through_all_reads()
    {
        var game = GameFixture.Game("awaitingPayment");
        var store = new Mock<ICosmosPointReader>(MockBehavior.Strict);
        store.Setup(s => s.ReadAsync<GameDocument>(DataTests.Owner, game.Id, "0:-1#1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DocumentRead<GameDocument>(game, "0:-1#2", "same"));
        store.Setup(s => s.ReadAsync<RoundDocument>(DataTests.Owner, $"round:{GameFixture.Id:D}:1", "0:-1#2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DocumentRead<RoundDocument>(GameFixture.Round("awaitingPayment"), "0:-1#3", "round"));
        store.Setup(s => s.ReadAsync<GameDocument>(DataTests.Owner, game.Id, "0:-1#3", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DocumentRead<GameDocument>(game, "0:-1#4", "same"));
        using var server = Server(store.Object);
        using var client = Client(server);
        client.DefaultRequestHeaders.Add(ReadSession.HeaderName, Convert.ToBase64String(Encoding.UTF8.GetBytes("0:-1#1")).TrimEnd('='));
        var response = await client.PostAsync(Path(), new StringContent("{\"totalYen\":13800}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("0:-1#4", ReadSession.Decode(response.Headers.GetValues(ReadSession.HeaderName).Single()));
        store.VerifyAll();
    }

    private static async Task Error(HttpResponseMessage response, int status, string code)
    {
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Null(response.Headers.ETag);
        Assert.True(response.Headers.CacheControl!.NoStore);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var error = json.RootElement.GetProperty("error");
        Assert.Equal(code, error.GetProperty("code").GetString());
        Assert.Equal(0, error.GetProperty("details").GetArrayLength());
        Assert.True(Guid.TryParse(error.GetProperty("requestId").GetString(), out _));
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
    }
}
