using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc;
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
using Xunit;

namespace RollAndPay.Api.Tests;

public sealed class GameHttpTests(TokenFixture tokens) : IClassFixture<TokenFixture>
{
    private IHost Server(ICosmosPointReader store)
    {
        var pipeline = new ApiHttpPipeline(tokens.Validator(), NullLogger<ApiHttpPipeline>.Instance);
        var functions = new GameReadFunctions(new GameReader(store));
        return new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(s => s.AddMvc())
            .Configure(app => app.Run(context => pipeline.InvokeAsync(context, false, async () =>
            {
                var result = context.Request.Path == "/api/v1/games/active" ? await functions.Active(context.Request)
                    : await functions.Detail(context.Request, context.Request.Path.Value!.Split('/').Last());
                await result.ExecuteResultAsync(new ActionContext(context, new RouteData(), new ActionDescriptor()));
            })))).Start();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Public_response_contains_only_game_and_current_round_with_strong_revision_ETag(bool active)
    {
        var store = GameReaderTests.Store(GameFixture.Game(), GameFixture.Round(), GameFixture.Control(GameFixture.Id));
        using var server = Server(store.Object);
        using var client = server.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.Token());
        client.DefaultRequestHeaders.Add("X-Owner-Key", "untrusted-other-owner");
        var response = await client.GetAsync($"/api/v1/games/{(active ? "active" : GameFixture.Id.ToString().ToUpperInvariant())}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(GameEtag.Create(GameFixture.Id, 4), response.Headers.ETag!.ToString());
        Assert.False(response.Headers.ETag.IsWeak);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal(active ? "0:-1#4" : "0:-1#2", ReadSession.Decode(response.Headers.GetValues(ReadSession.HeaderName).Single()));
        var text = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(text);
        var game = active ? json.RootElement.GetProperty("game") : json.RootElement;
        Assert.Equal(11, game.EnumerateObject().Count());
        Assert.Equal(7, game.GetProperty("currentRound").EnumerateObject().Count());
        Assert.Equal("当時の名前A", game.GetProperty("participants")[0].GetProperty("displayName").GetString());
        Assert.Equal(JsonValueKind.Null, game.GetProperty("currentRound").GetProperty("rolls")[1].GetProperty("value").ValueKind);
        foreach (var field in new[] { "ownerKey", "schemaVersion", "deletedAt", "currentRoundNumber", "_etag", "internal-game-etag" })
            Assert.DoesNotContain(field, text);
        Assert.Contains(ReadSession.HeaderName, string.Join(",", response.Headers.GetValues("Access-Control-Expose-Headers")));
    }

    [Fact]
    public async Task No_active_game_is_null_with_no_ETag_and_has_the_latest_control_session()
    {
        using var server = Server(GameReaderTests.Store(control: GameFixture.Control()).Object);
        using var client = server.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.Token());
        var response = await client.GetAsync("/api/v1/games/active");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"game\":null}", await response.Content.ReadAsStringAsync());
        Assert.Null(response.Headers.ETag);
        Assert.Equal("0:-1#4", ReadSession.Decode(response.Headers.GetValues(ReadSession.HeaderName).Single()));
    }

    [Theory]
    [InlineData("not-a-uuid")]
    [InlineData(" 7c56e8d0-1a58-4c45-8ef3-6505ad5c0a01")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("7c56e8d01a584c458ef36505ad5c0a01")]
    [InlineData("7c56e8d0-1a58-4c45-8ef3-6505ad5c0a01?extra=1")]
    [InlineData("active?ownerKey=other")]
    public async Task Invalid_ID_or_query_is_400_before_any_DB_call(string id)
    {
        var store = new Mock<ICosmosPointReader>(MockBehavior.Strict);
        using var server = Server(store.Object);
        using var client = server.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.Token());
        var response = await client.GetAsync($"/api/v1/games/{id}");
        await AssertError(response, 400, "INVALID_REQUEST");
        store.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("missing", 401)]
    [InlineData("other-user", 403)]
    [InlineData("body", 400)]
    [InlineData("session", 400)]
    public async Task Authentication_and_format_rejections_do_not_touch_DB(string fault, int status)
    {
        var store = new Mock<ICosmosPointReader>(MockBehavior.Strict);
        using var server = Server(store.Object);
        using var client = server.GetTestClient();
        if (fault != "missing") client.DefaultRequestHeaders.Authorization = new("Bearer",
            tokens.Token(d => { if (fault == "other-user") d.Claims["oid"] = Guid.NewGuid().ToString(); }));
        if (fault == "session") client.DefaultRequestHeaders.Add(ReadSession.HeaderName, "invalid=");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/games/{GameFixture.Id:D}");
        if (fault == "body") request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        var response = await client.SendAsync(request);
        Assert.Equal(status, (int)response.StatusCode);
        store.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("missing", 404, "GAME_NOT_FOUND")]
    [InlineData("owner", 404, "GAME_NOT_FOUND")]
    [InlineData("deleted", 410, "GAME_DELETED")]
    [InlineData("pointer", 503, "TEMPORARILY_UNAVAILABLE")]
    public async Task Error_contract_never_discloses_game_data_or_an_ETag(string fault, int status, string code)
    {
        var game = fault == "missing" ? null : GameFixture.Game(fault == "deleted" ? "deleted" : "rolling");
        if (fault == "owner") game = game! with { OwnerKey = "other" };
        using var server = Server(GameReaderTests.Store(game, GameFixture.Round()).Object);
        using var client = server.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.Token());
        var response = await client.GetAsync($"/api/v1/games/{(fault == "pointer" ? "active" : GameFixture.Id.ToString())}");
        await AssertError(response, status, code);
        Assert.Null(response.Headers.ETag);
        if (status == 503) Assert.Equal(TimeSpan.FromSeconds(5), response.Headers.RetryAfter!.Delta);
    }

    private static async Task AssertError(HttpResponseMessage response, int status, string code)
    {
        Assert.Equal(status, (int)response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("当時", text);
        using var json = JsonDocument.Parse(text);
        var error = json.RootElement.GetProperty("error");
        Assert.Equal(code, error.GetProperty("code").GetString());
        Assert.True(Guid.TryParse(error.GetProperty("requestId").GetString(), out _));
        Assert.Equal(0, error.GetProperty("details").GetArrayLength());
    }
}
