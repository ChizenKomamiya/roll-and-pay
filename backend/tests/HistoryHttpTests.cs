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
using RollAndPay.Api.History;
using RollAndPay.Api.Http;
using Xunit;

namespace RollAndPay.Api.Tests;

public sealed class HistoryHttpTests(TokenFixture tokens) : IClassFixture<TokenFixture>
{
    private IHost Server(IHistoryStore history, ICosmosPointReader points)
    {
        var pipeline = new ApiHttpPipeline(tokens.Validator(), NullLogger<ApiHttpPipeline>.Instance);
        var functions = new HistoryReadFunctions(new HistoryReader(history, points));
        return new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(s => s.AddMvc())
            .Configure(app => app.Run(context => pipeline.InvokeAsync(context, false, async () =>
            {
                var result = context.Request.Path == "/api/v1/games" ? await functions.List(context.Request)
                    : await functions.Detail(context.Request, context.Request.Path.Value!.Split('/')[4]);
                await result.ExecuteResultAsync(new ActionContext(context, new RouteData(), new ActionDescriptor()));
            })))).Start();
    }

    [Fact]
    public async Task Detail_exposes_only_saved_history_fields_with_game_ETag_and_session()
    {
        var history = new Mock<IHistoryStore>(MockBehavior.Strict);
        var points = HistoryReaderTests.Points(GameFixture.Game("completed"));
        using var server = Server(history.Object, points.Object);
        using var client = server.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.Token());
        client.DefaultRequestHeaders.Add(ReadSession.HeaderName, DataTests.Encode("0:-1#1"));
        client.DefaultRequestHeaders.Add("X-Owner-Key", "other");
        var response = await client.GetAsync($"/api/v1/games/{GameFixture.Id.ToString().ToUpperInvariant()}/history-detail");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(GameEtag.Create(GameFixture.Id, 4), response.Headers.ETag!.ToString());
        Assert.False(response.Headers.ETag.IsWeak);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal("0:-1#2", ReadSession.Decode(response.Headers.GetValues(ReadSession.HeaderName).Single()));
        var text = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(text);
        Assert.Equal(8, json.RootElement.EnumerateObject().Count());
        Assert.True(json.RootElement.GetProperty("canDelete").GetBoolean());
        Assert.Equal("当時の名前A", json.RootElement.GetProperty("participants")[0].GetProperty("displayName").GetString());
        Assert.Equal(3800, json.RootElement.GetProperty("payment").GetProperty("totalYen").GetInt64());
        foreach (var field in new[] { "currentRound", "roundNumber", "rolls", "tiedParticipantIds", "ownerKey", "schemaVersion", "deletedAt", "internal-etag" })
            Assert.DoesNotContain(field, text);
        points.Verify(p => p.ReadAsync<GameDocument>(DataTests.Owner, $"game:{GameFixture.Id:D}", "0:-1#1", It.IsAny<CancellationToken>()), Times.Once);
        points.VerifyNoOtherCalls(); history.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(50)]
    public async Task Pages_order_by_creation_then_ID_descending_and_include_zero_loss(int limit)
    {
        var zero = Saved(2) with { Payment = GameFixture.Game("completed").Payment! with { TotalYen = 2000, LossYen = 0,
            LoserPaymentYen = 1000, Shares = [new() { ParticipantId = "p001", AmountYen = 1000 }, new() { ParticipantId = "p002", AmountYen = 1000 }] } };
        var history = new MemoryHistory([Saved(1), zero, Saved(3), Saved(4) with { Status = "deleted" },
            Saved(5) with { Status = "cancelled" }, Saved(6) with { OwnerKey = "other" },
            Saved(7) with { CreatedAt = GameFixture.Now.AddMinutes(-1) }]);
        using var server = Server(history, new Mock<ICosmosPointReader>(MockBehavior.Strict).Object);
        using var client = server.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.Token());
        string? cursor = null;
        var ids = new List<Guid>();
        do
        {
            var response = await client.GetAsync($"/api/v1/games?limit={limit}" + (cursor is null ? "" : $"&cursor={cursor}"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Null(response.Headers.ETag);
            Assert.True(response.Headers.CacheControl!.NoStore);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var items = json.RootElement.GetProperty("items");
            Assert.InRange(items.GetArrayLength(), 1, limit);
            foreach (var item in items.EnumerateArray())
            {
                ids.Add(item.GetProperty("gameId").GetGuid());
                Assert.Equal(6, item.EnumerateObject().Count());
                Assert.Equal("当時の名前A", item.GetProperty("loser").GetProperty("displayName").GetString());
                if (item.GetProperty("gameId").GetGuid() == zero.GameId) Assert.Equal(0, item.GetProperty("lossYen").GetInt64());
            }
            cursor = json.RootElement.GetProperty("nextCursor").GetString();
            client.DefaultRequestHeaders.Remove(ReadSession.HeaderName);
            client.DefaultRequestHeaders.Add(ReadSession.HeaderName, response.Headers.GetValues(ReadSession.HeaderName));
        } while (cursor is not null);
        Assert.Equal(new[] { Saved(3).GameId, zero.GameId, Saved(1).GameId, Saved(7).GameId }, ids);
        Assert.All(history.Sessions.Skip(1), value => Assert.Equal("0:-1#3", value));
    }

    [Fact]
    public async Task Empty_history_has_null_cursor_and_a_session_and_default_limit_twenty()
    {
        var history = new Mock<IHistoryStore>();
        history.Setup(h => h.QueryAsync(DataTests.Owner, new HistoryQuery(20, null), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HistoryRead([], "0:-1#3"));
        using var server = Server(history.Object, new Mock<ICosmosPointReader>(MockBehavior.Strict).Object);
        using var client = server.GetTestClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.Token());
        var response = await client.GetAsync("/api/v1/games");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"items\":[],\"nextCursor\":null}", await response.Content.ReadAsStringAsync());
        Assert.Equal("0:-1#3", ReadSession.Decode(response.Headers.GetValues(ReadSession.HeaderName).Single()));
        history.VerifyAll();
    }

    [Theory]
    [InlineData(false, "missing", 401, "UNAUTHENTICATED")]
    [InlineData(true, "missing", 401, "UNAUTHENTICATED")]
    [InlineData(false, "foreign-user", 403, "FORBIDDEN")]
    [InlineData(true, "foreign-user", 403, "FORBIDDEN")]
    [InlineData(false, "body", 400, "INVALID_REQUEST")]
    [InlineData(true, "body", 400, "INVALID_REQUEST")]
    [InlineData(false, "session", 400, "INVALID_READ_SESSION")]
    [InlineData(true, "query", 400, "INVALID_REQUEST")]
    [InlineData(true, "id", 400, "INVALID_REQUEST")]
    [InlineData(false, "cursor", 400, "INVALID_CURSOR")]
    [InlineData(false, "query", 400, "INVALID_REQUEST")]
    public async Task Rejections_happen_before_any_DB_access(bool detail, string fault, int status, string code)
    {
        var history = new Mock<IHistoryStore>(MockBehavior.Strict); var points = new Mock<ICosmosPointReader>(MockBehavior.Strict);
        using var server = Server(history.Object, points.Object);
        using var client = server.GetTestClient();
        if (fault != "missing") client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.Token(d =>
            { if (fault == "foreign-user") d.Claims["oid"] = Guid.NewGuid().ToString(); }));
        if (fault == "session") client.DefaultRequestHeaders.Add(ReadSession.HeaderName, "invalid=");
        var path = detail ? $"/api/v1/games/{(fault == "id" ? "invalid" : GameFixture.Id.ToString())}/history-detail" : "/api/v1/games";
        if (fault == "query") path += "?ownerKey=other";
        if (fault == "cursor") path += "?cursor=invalid=";
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (fault == "body") request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        await AssertError(await client.SendAsync(request), status, code);
        history.VerifyNoOtherCalls(); points.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("deleted", 410, "GAME_DELETED")]
    [InlineData("rolling", 409, "INVALID_GAME_STATE")]
    [InlineData("missing", 404, "GAME_NOT_FOUND")]
    [InlineData("corrupt", 503, "TEMPORARILY_UNAVAILABLE")]
    public async Task Detail_error_contract_omits_data_and_ETag(string fault, int status, string code)
    {
        var game = fault == "missing" ? null : GameFixture.Game(fault == "corrupt" ? "completed" : fault);
        if (fault == "corrupt") game = game! with { Payment = null };
        using var server = Server(new Mock<IHistoryStore>(MockBehavior.Strict).Object, HistoryReaderTests.Points(game).Object);
        using var client = server.GetTestClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.Token());
        await AssertError(await client.GetAsync($"/api/v1/games/{GameFixture.Id:D}/history-detail"), status, code);
    }

    private static async Task AssertError(HttpResponseMessage response, int status, string code)
    {
        Assert.Equal(status, (int)response.StatusCode); Assert.Null(response.Headers.ETag);
        Assert.True(response.Headers.CacheControl!.NoStore);
        if (status == 503) Assert.Equal(TimeSpan.FromSeconds(5), response.Headers.RetryAfter!.Delta);
        var text = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain("当時", text);
        using var json = JsonDocument.Parse(text); var error = json.RootElement.GetProperty("error");
        Assert.Equal(code, error.GetProperty("code").GetString());
        Assert.True(Guid.TryParse(error.GetProperty("requestId").GetString(), out _));
        Assert.Empty(error.GetProperty("details").EnumerateArray());
    }

    internal static GameDocument Saved(int number)
    {
        var id = Guid.Parse($"00000000-0000-0000-0000-{number:D12}");
        return GameFixture.Game("completed") with { Id = $"game:{id:D}", GameId = id };
    }

    private sealed class MemoryHistory(List<GameDocument> games) : IHistoryStore
    {
        public List<string?> Sessions { get; } = [];
        public Task<HistoryRead> QueryAsync(string owner, HistoryQuery query, string? sessionToken, CancellationToken token)
        {
            Sessions.Add(sessionToken);
            var rows = games.Where(g => g.OwnerKey == owner && g.Status == "completed")
                .Where(g => query.Boundary is null || g.CreatedAt < query.Boundary.CreatedAt ||
                    (g.CreatedAt == query.Boundary.CreatedAt && string.CompareOrdinal(g.GameId.ToString("D"), query.Boundary.GameId.ToString("D")) < 0))
                .OrderByDescending(g => g.CreatedAt).ThenByDescending(g => g.GameId.ToString("D"), StringComparer.Ordinal)
                .Take(query.Limit + 1).ToArray();
            return Task.FromResult(new HistoryRead(rows, "0:-1#3"));
        }
    }
}
