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
using RollAndPay.Api.Payments;
using RollAndPay.Seed;
using Xunit;

namespace RollAndPay.Api.Tests;

public sealed class PaymentWorkflowHttpTests(TokenFixture tokens) : IClassFixture<TokenFixture>
{
    private IHost Server(CreateStore store, IDie? die = null) => new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(s => s.AddMvc())
        .Configure(app => app.Run(context => new ApiHttpPipeline(tokens.Validator(), NullLogger<ApiHttpPipeline>.Instance).InvokeAsync(context, false, async () =>
        {
            var request = context.Request; var path = request.Path.Value!; var reader = new GameReader(store);
            var id = path.Split('/').ElementAtOrDefault(4)!;
            IActionResult result = path switch
            {
                "/api/v1/statistics/losses" => await new LossTotalsFunction(new(store)).Run(request),
                "/api/v1/games" => await new GameCreateFunction(store.Creator()).Run(request),
                _ when request.Method == "DELETE" => await new HistoryDeleteFunction(new(store, store)).Run(request, id),
                _ when path.EndsWith("/payment-preview") => await new PaymentPreviewFunction(reader).Run(request, id),
                _ when path.EndsWith("/payment") => await new PaymentSaveFunction(new(store, store)).Run(request, id),
                _ when path.EndsWith("/rolls") => await new GameDiceFunctions(new(store, store, die ?? new SequenceDie())).Roll(request, id),
                _ => await new GameDiceFunctions(new(store, store, new SequenceDie())).NextRound(request, id)
            };
            await result.ExecuteResultAsync(new ActionContext(context, new RouteData(), new ActionDescriptor()));
        })))).Start();

    [Fact]
    public async Task Authenticated_create_tie_redraw_preview_save_and_statistics_share_one_consistent_result()
    {
        var store = new CreateStore();
        var data = InitialData.Build(DataTests.Owner, Enumerable.Range(1, 4).Select(n => DataTests.Person($"p00{n}")).ToArray(), GameFixture.Now);
        store.Put(data.Roster, "roster"); store.Put(data.LossTotals, "lossTotals");
        using var host = Server(store, new SequenceDie(1, 1, 1, 1, 1, 2, 3, 4)); using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.Token());
        async Task<HttpResponseMessage> Post(string path, string body, string? etag = null, string? key = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
            if (etag is not null) request.Headers.TryAddWithoutValidation("If-Match", etag);
            var response = await client.SendAsync(request);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync()); Assert.True(response.Headers.CacheControl!.NoStore);
            if (response.Headers.TryGetValues("X-Read-Session", out var session))
            { client.DefaultRequestHeaders.Remove("X-Read-Session"); client.DefaultRequestHeaders.Add("X-Read-Session", session.Single()); }
            return response;
        }
        var initial = await client.GetAsync("/api/v1/statistics/losses");
        using (var json = JsonDocument.Parse(await initial.Content.ReadAsStringAsync())) Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("updatedAt").ValueKind);
        var created = await Post("/api/v1/games", "{\"participantIds\":[\"p001\",\"p002\",\"p003\",\"p004\"]}");
        using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = createdBody.RootElement.GetProperty("gameId").GetString(); var path = $"/api/v1/games/{id}";
        var tie = await Post(path + "/rolls", "{\"roundNumber\":1,\"mode\":\"remaining\"}", created.Headers.ETag!.Tag);
        using (var json = JsonDocument.Parse(await tie.Content.ReadAsStringAsync())) Assert.Equal("tie", json.RootElement.GetProperty("status").GetString());
        var next = await Post(path + "/rounds", "{\"previousRoundNumber\":1}", tie.Headers.ETag!.Tag);
        var decided = await Post(path + "/rolls", "{\"roundNumber\":2,\"mode\":\"remaining\"}", next.Headers.ETag!.Tag);
        var preview = await Post(path + "/payment-preview", "{\"totalYen\":13800}");
        Assert.Equal(decided.Headers.ETag!.Tag, preview.Headers.ETag!.Tag);
        using var previewBody = JsonDocument.Parse(await preview.Content.ReadAsStringAsync());
        Assert.Equal(1800, previewBody.RootElement.GetProperty("payment").GetProperty("lossYen").GetInt64());
        var key = Guid.NewGuid().ToString(); var saved = await Post(path + "/payment", "{\"totalYen\":13800}", preview.Headers.ETag.Tag, key);
        var replay = await Post(path + "/payment", "{\"totalYen\":13800}", preview.Headers.ETag.Tag, key);
        Assert.Equal(await saved.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single());
        var statistics = await client.GetAsync("/api/v1/statistics/losses"); Assert.Equal(200, (int)statistics.StatusCode);
        using var body = JsonDocument.Parse(await statistics.Content.ReadAsStringAsync());
        var entries = body.RootElement.GetProperty("participants").EnumerateArray().ToArray();
        Assert.Equal("1800", entries[0].GetProperty("totalLossYen").GetString());
        Assert.All(entries.Skip(1), p => Assert.Equal("0", p.GetProperty("totalLossYen").GetString()));
        Assert.Equal("tied", ((RoundDocument)store.Documents[$"round:{id}:1"].Document).Status);
        using var delete = new HttpRequestMessage(HttpMethod.Delete, path);
        delete.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        delete.Headers.TryAddWithoutValidation("If-Match", saved.Headers.ETag!.Tag);
        var deleted = await client.SendAsync(delete); Assert.Equal(200, (int)deleted.StatusCode);
        client.DefaultRequestHeaders.Remove("X-Read-Session");
        client.DefaultRequestHeaders.Add("X-Read-Session", deleted.Headers.GetValues("X-Read-Session").Single());
        var oldSave = await Post(path + "/payment", "{\"totalYen\":13800}", preview.Headers.ETag.Tag, key);
        Assert.Equal("true", oldSave.Headers.GetValues("Idempotency-Replayed").Single());
        Assert.Equal(await saved.Content.ReadAsStringAsync(), await oldSave.Content.ReadAsStringAsync());
        using var afterDelete = JsonDocument.Parse(await (await client.GetAsync("/api/v1/statistics/losses")).Content.ReadAsStringAsync());
        Assert.All(afterDelete.RootElement.GetProperty("participants").EnumerateArray(), p => Assert.Equal("0", p.GetProperty("totalLossYen").GetString()));
        Assert.Equal("deleted", ((GameDocument)store.Documents[$"game:{id}"].Document).Status);
        Assert.Equal("tied", ((RoundDocument)store.Documents[$"round:{id}:1"].Document).Status);
    }

    [Theory]
    [InlineData("auth", 401)] [InlineData("forbidden", 403)] [InlineData("query", 400)]
    [InlineData("body", 400)] [InlineData("chunked-body", 400)] [InlineData("session", 400)]
    public async Task Statistics_rejects_invalid_requests_before_DB(string fault, int status)
    {
        var store = PaymentSaveTests.Store(); using var host = Server(store); using var client = host.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/statistics/losses" + (fault == "query" ? "?x=1" : ""));
        if (fault != "auth") request.Headers.Authorization = new("Bearer", tokens.Token(fault == "forbidden" ? d => d.Claims["oid"] = Guid.NewGuid().ToString() : null));
        if (fault == "body") request.Content = new StringContent("{}");
        if (fault == "chunked-body") { request.Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes("{}"))); request.Headers.TransferEncodingChunked = true; }
        if (fault == "session") request.Headers.Add("X-Read-Session", "invalid=");
        var result = await client.SendAsync(request); Assert.Equal(status, (int)result.StatusCode); Assert.Equal(0, store.Reads);
    }
}
