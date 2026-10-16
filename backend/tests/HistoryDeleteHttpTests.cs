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
using RollAndPay.Api.History;
using RollAndPay.Api.Http;
using Xunit;

namespace RollAndPay.Api.Tests;

public sealed class HistoryDeleteHttpTests(TokenFixture tokens) : IClassFixture<TokenFixture>
{
    private IHost Server(CreateStore store) => new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(s => s.AddMvc())
        .Configure(app => app.Run(context => new ApiHttpPipeline(tokens.Validator(), NullLogger<ApiHttpPipeline>.Instance).InvokeAsync(context, false, async () =>
        {
            var request = context.Request; var id = request.Path.Value!.Split('/').ElementAtOrDefault(4)!;
            var history = new HistoryReadFunctions(new(new CurrentHistory(store), store));
            IActionResult result = request.Path.Value switch
            {
                "/api/v1/statistics/losses" => await new LossTotalsFunction(new(store)).Run(request),
                "/api/v1/games" => await history.List(request),
                _ when request.Path.Value.EndsWith("/history-detail") => await history.Detail(request, id),
                _ when request.Method == "DELETE" => await new HistoryDeleteFunction(new(store, store)).Run(request, id),
                _ => await new GameReadFunctions(new(store)).Detail(context.Request, id)
            };
            await result.ExecuteResultAsync(new ActionContext(context, new RouteData(), new ActionDescriptor()));
        })))).Start();

    private HttpRequestMessage Request(string? etag = null, string? key = null, string? id = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/games/{id ?? GameFixture.Id.ToString("D")}");
        request.Headers.Authorization = new("Bearer", tokens.Token());
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        request.Headers.TryAddWithoutValidation("If-Match", etag ?? HistoryDeleteTests.Etag);
        return request;
    }

    [Theory]
    [InlineData("missing-auth", 401)] [InlineData("forbidden", 403)]
    [InlineData("missing-key", 428)] [InlineData("bad-key", 400)] [InlineData("duplicate-key", 400)]
    [InlineData("missing-etag", 428)] [InlineData("duplicate-etag", 400)]
    [InlineData("bad-id", 400)] [InlineData("empty-id", 400)] [InlineData("query", 400)] [InlineData("session", 400)]
    [InlineData("body", 400)] [InlineData("chunked-body", 400)] [InlineData("whitespace", 400)]
    public async Task Invalid_requests_and_authentication_failures_do_not_access_DB(string fault, int status)
    {
        var store = new CreateStore(); using var server = Server(store); using var client = server.GetTestClient();
        using var request = Request(id: fault == "bad-id" ? "bad" : fault == "empty-id" ? Guid.Empty.ToString() : null);
        switch (fault)
        {
            case "missing-auth": request.Headers.Authorization = null; break;
            case "forbidden": request.Headers.Authorization = new("Bearer", tokens.Token(d => d.Claims["oid"] = Guid.NewGuid().ToString())); break;
            case "missing-key": request.Headers.Remove("Idempotency-Key"); break;
            case "bad-key": request.Headers.Remove("Idempotency-Key"); request.Headers.Add("Idempotency-Key", "bad"); break;
            case "duplicate-key": request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString()); break;
            case "missing-etag": request.Headers.Remove("If-Match"); break;
            case "duplicate-etag": request.Headers.TryAddWithoutValidation("If-Match", HistoryDeleteTests.Etag); break;
            case "query": request.RequestUri = new(request.RequestUri!.OriginalString + "?x=1", UriKind.Relative); break;
            case "session": request.Headers.Add("X-Read-Session", "bad="); break;
            case "body": request.Content = new StringContent("{}"); break;
            case "whitespace": request.Content = new StringContent(" "); break;
            case "chunked-body": request.Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes("{}"))); request.Headers.TransferEncodingChunked = true; break;
        }
        var result = await client.SendAsync(request);
        Assert.Equal(status, (int)result.StatusCode); Assert.Equal(0, store.Reads); Assert.Equal(0, store.Writes);
    }

    [Theory]
    [InlineData("*")] [InlineData("W/\"tag\"")] [InlineData("\"a\", \"b\"")] [InlineData("tag")]
    [InlineData("\"a b\"")] [InlineData("\"\"\"")]
    public async Task Rejects_invalid_strong_ETags_before_DB(string etag)
    {
        var store = new CreateStore(); using var server = Server(store); using var client = server.GetTestClient();
        using var request = Request(etag: etag);
        Assert.Equal(400, (int)(await client.SendAsync(request)).StatusCode); Assert.Equal(0, store.Reads);
    }

    [Theory]
    [InlineData(3800)] [InlineData(4000)]
    public async Task Detail_delete_replay_and_refetch_remove_last_history_and_zero_the_totals(long total)
    {
        var store = await HistoryDeleteTests.Store(total); using var server = Server(store); using var client = server.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.Token());
        var detail = await client.GetAsync($"/api/v1/games/{GameFixture.Id:D}/history-detail");
        Assert.Equal(200, (int)detail.StatusCode);
        var key = Guid.NewGuid().ToString();
        using var request = Request(etag: detail.Headers.ETag!.Tag, key: key.ToUpperInvariant(), id: GameFixture.Id.ToString().ToUpperInvariant());
        request.Headers.Add("X-Owner-Key", "other");
        var response = await client.SendAsync(request); var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(200, (int)response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal(GameEtag.Create(GameFixture.Id, 6), response.Headers.ETag!.Tag);
        Assert.Equal("false", response.Headers.GetValues("Idempotency-Replayed").Single());
        Assert.Contains("Idempotency-Replayed", response.Headers.GetValues("Access-Control-Expose-Headers").Single());
        foreach (var field in new[] { "ownerKey", "schemaVersion", "rolls", "etag-", "当時" }) Assert.DoesNotContain(field, body);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(6, json.RootElement.EnumerateObject().Count());
        Assert.Equal("deleted", json.RootElement.GetProperty("status").GetString());
        Assert.Equal(total == 4000 ? 0 : 1800, json.RootElement.GetProperty("removedLoss").GetProperty("amountYen").GetInt64());
        var session = response.Headers.GetValues("X-Read-Session").Single();
        client.DefaultRequestHeaders.Add("X-Read-Session", session);
        using var again = Request(key: key); var replay = await client.SendAsync(again);
        Assert.Equal(body, await replay.Content.ReadAsStringAsync()); Assert.Equal(response.Headers.ETag, replay.Headers.ETag);
        Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single()); Assert.Equal(1, store.Writes);
        using var otherKey = Request(); Assert.Equal(410, (int)(await client.SendAsync(otherKey)).StatusCode);
        Assert.Equal(410, (int)(await client.GetAsync($"/api/v1/games/{GameFixture.Id:D}/history-detail")).StatusCode);
        Assert.Equal(410, (int)(await client.GetAsync($"/api/v1/games/{GameFixture.Id:D}")).StatusCode);
        using var list = JsonDocument.Parse(await (await client.GetAsync("/api/v1/games")).Content.ReadAsStringAsync());
        Assert.Empty(list.RootElement.GetProperty("items").EnumerateArray()); Assert.Equal(JsonValueKind.Null, list.RootElement.GetProperty("nextCursor").ValueKind);
        var statistics = await client.GetAsync("/api/v1/statistics/losses"); Assert.Equal(200, (int)statistics.StatusCode);
        using var totals = JsonDocument.Parse(await statistics.Content.ReadAsStringAsync());
        Assert.All(totals.RootElement.GetProperty("participants").EnumerateArray(), p => Assert.Equal("0", p.GetProperty("totalLossYen").GetString()));
    }

    private sealed class CurrentHistory(CreateStore store) : IHistoryStore
    {
        public Task<HistoryRead> QueryAsync(string owner, HistoryQuery query, string? sessionToken, CancellationToken cancellationToken) =>
            Task.FromResult(new HistoryRead(store.Documents.Values.Select(d => d.Document).OfType<GameDocument>()
                .Where(g => g.OwnerKey == owner && g.Status == "completed").OrderByDescending(g => g.CreatedAt)
                .ThenByDescending(g => g.GameId.ToString("D"), StringComparer.Ordinal).Take(query.Limit + 1).ToArray(), sessionToken ?? "0:-1#1"));
    }
}
