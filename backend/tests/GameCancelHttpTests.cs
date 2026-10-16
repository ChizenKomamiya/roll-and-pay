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
using RollAndPay.Api.Functions;
using RollAndPay.Api.Games;
using RollAndPay.Api.Http;
using Xunit;

namespace RollAndPay.Api.Tests;

public sealed class GameCancelHttpTests(TokenFixture tokens) : IClassFixture<TokenFixture>
{
    private IHost Server(CreateStore store) => new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(s => s.AddMvc())
        .Configure(app => app.Run(context => new ApiHttpPipeline(tokens.Validator(), NullLogger<ApiHttpPipeline>.Instance).InvokeAsync(context, false, async () =>
        {
            var id = context.Request.Path.Value!.Split('/')[4];
            var result = await new GameCancelFunction(new GameCanceller(store, store)).Run(context.Request, id);
            await result.ExecuteResultAsync(new ActionContext(context, new RouteData(), new ActionDescriptor()));
        })))).Start();

    private HttpRequestMessage Request(string body = "{}", string? etag = null, string? key = null, string? id = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/games/{id ?? GameFixture.Id.ToString("D")}/cancel")
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new("Bearer", tokens.Token());
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString("D"));
        request.Headers.TryAddWithoutValidation("If-Match", etag ?? GameCancelTests.Etag);
        return request;
    }

    [Theory]
    [InlineData("[]", 422)]
    [InlineData("null", 422)]
    [InlineData("1", 422)]
    [InlineData("{\"reason\":null}", 422)]
    [InlineData("{\"reason\":1,\"reason\":2}", 422)]
    [InlineData("{", 400)]
    [InlineData("", 400)]
    public async Task Body_must_be_an_empty_object_before_DB_access(string body, int status)
    {
        var store = GameCancelTests.Store(); using var server = Server(store); using var client = server.GetTestClient();
        using var request = Request(body); var result = await client.SendAsync(request);
        Assert.Equal(status, (int)result.StatusCode); Assert.Equal(0, store.Reads); Assert.Equal(0, store.Writes);
    }

    [Theory]
    [InlineData("*", 400)]
    [InlineData("W/\"tag\"", 400)]
    [InlineData("\"a\", \"b\"", 400)]
    [InlineData("tag", 400)]
    [InlineData("\"a b\"", 400)]
    [InlineData("\"\"\"", 400)]
    [InlineData("", 428)]
    [InlineData("\"other-tag\"", 412)]
    public async Task Requires_one_strong_etag_and_rejects_a_different_valid_tag_as_version_mismatch(string etag, int status)
    {
        var store = GameCancelTests.Store(); using var server = Server(store); using var client = server.GetTestClient();
        using var request = Request(etag: etag); var result = await client.SendAsync(request);
        Assert.Equal(status, (int)result.StatusCode); Assert.Equal(0, store.Writes);
        if (status == 400) Assert.Equal(0, store.Reads);
    }

    [Theory]
    [InlineData("missing-auth", 401)]
    [InlineData("forbidden", 403)]
    [InlineData("missing-key", 428)]
    [InlineData("bad-key", 400)]
    [InlineData("duplicate-key", 400)]
    [InlineData("missing-etag", 428)]
    [InlineData("duplicate-etag", 400)]
    [InlineData("bad-id", 400)]
    [InlineData("empty-id", 400)]
    [InlineData("query", 400)]
    [InlineData("session", 400)]
    [InlineData("media", 415)]
    [InlineData("oversize", 413)]
    [InlineData("stream-oversize", 413)]
    public async Task Invalid_requests_and_authentication_failures_do_not_access_DB(string fault, int status)
    {
        var store = GameCancelTests.Store(); using var server = Server(store); using var client = server.GetTestClient();
        using var request = Request(id: fault == "bad-id" ? "bad" : fault == "empty-id" ? Guid.Empty.ToString() : null);
        switch (fault)
        {
            case "missing-auth": request.Headers.Authorization = null; break;
            case "forbidden": request.Headers.Authorization = new("Bearer", tokens.Token(d => d.Claims["oid"] = Guid.NewGuid().ToString())); break;
            case "missing-key": request.Headers.Remove("Idempotency-Key"); break;
            case "bad-key": request.Headers.Remove("Idempotency-Key"); request.Headers.Add("Idempotency-Key", "bad"); break;
            case "duplicate-key": request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString()); break;
            case "missing-etag": request.Headers.Remove("If-Match"); break;
            case "duplicate-etag": request.Headers.TryAddWithoutValidation("If-Match", GameCancelTests.Etag); break;
            case "query": request.RequestUri = new(request.RequestUri!.OriginalString + "?x=1", UriKind.Relative); break;
            case "session": request.Headers.Add("X-Read-Session", "bad="); break;
            case "media": request.Content = new StringContent("{}", Encoding.UTF8, "text/plain"); break;
            case "oversize": request.Content = new StringContent(new string(' ', 16385), Encoding.UTF8, "application/json"); break;
            case "stream-oversize":
                request.Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(new string(' ', 16385))));
                request.Content.Headers.ContentType = new("application/json"); request.Headers.TransferEncodingChunked = true; break;
        }
        var result = await client.SendAsync(request);
        Assert.Equal(status, (int)result.StatusCode); Assert.Equal(0, store.Reads); Assert.Equal(0, store.Writes);
    }

    [Fact]
    public async Task HTTP_success_and_replay_preserve_body_etag_and_headers_with_normalized_id_and_latest_session()
    {
        var store = GameCancelTests.Store("tie"); using var server = Server(store); using var client = server.GetTestClient();
        var key = Guid.NewGuid().ToString("D");
        using var request = Request(key: key.ToUpperInvariant(), id: GameFixture.Id.ToString("D").ToUpperInvariant());
        var first = await client.SendAsync(request); var body = await first.Content.ReadAsStringAsync();
        Assert.Equal(200, (int)first.StatusCode); Assert.True(first.Headers.CacheControl!.NoStore);
        Assert.Equal(GameEtag.Create(GameFixture.Id, 5), first.Headers.ETag!.Tag);
        Assert.Equal("false", first.Headers.GetValues("Idempotency-Replayed").Single()); Assert.Null(first.Headers.Location);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("cancelled", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("tied", json.RootElement.GetProperty("currentRound").GetProperty("status").GetString());
        Assert.DoesNotContain("ownerKey", body); Assert.DoesNotContain("etag-", body); Assert.DoesNotContain("schemaVersion", body);
        using var again = Request(" { } ", key: key);
        again.Headers.Add("X-Read-Session", first.Headers.GetValues("X-Read-Session").Single());
        var replay = await client.SendAsync(again);
        Assert.Equal(200, (int)replay.StatusCode); Assert.Equal(body, await replay.Content.ReadAsStringAsync());
        Assert.Equal(first.Headers.ETag, replay.Headers.ETag); Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single());
        Assert.StartsWith("0:-1#", ReadSession.Decode(replay.Headers.GetValues("X-Read-Session").Single()));
        Assert.Contains("Idempotency-Replayed", replay.Headers.GetValues("Access-Control-Expose-Headers").Single());
        Assert.Equal(1, store.Writes);
        using var stale = Request(); var conflict = await client.SendAsync(stale);
        Assert.Equal(412, (int)conflict.StatusCode); Assert.Null(conflict.Headers.ETag);
        using var fresh = Request(etag: GameEtag.Create(GameFixture.Id, 5));
        Assert.Equal(409, (int)(await client.SendAsync(fresh)).StatusCode);
    }
}
