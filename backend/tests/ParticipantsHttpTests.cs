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
using RollAndPay.Api.Http;
using Xunit;

namespace RollAndPay.Api.Tests;

public sealed class ParticipantsHttpTests(TokenFixture tokens) : IClassFixture<TokenFixture>
{
    private IHost Server(IRosterStore store)
    {
        var pipeline = new ApiHttpPipeline(tokens.Validator(), NullLogger<ApiHttpPipeline>.Instance);
        return new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(s => s.AddMvc())
            .Configure(app => app.Run(context => pipeline.InvokeAsync(context, false, async () =>
            {
                var result = await new ParticipantsFunction(store).Run(context.Request);
                await result.ExecuteResultAsync(new ActionContext(context, new RouteData(), new ActionDescriptor()));
            })))).Start();
    }

    [Fact]
    public async Task Returns_only_active_people_in_stable_order_and_hands_session_to_a_new_instance()
    {
        var roster = DataTests.Roster(DataTests.Person("p003", order: 1), DataTests.Person("p001", order: 1),
            DataTests.Person("p002", order: 0), DataTests.Person("p004", active: false));
        var first = new Mock<IRosterStore>(MockBehavior.Strict);
        first.Setup(s => s.ReadAsync(DataTests.Owner, null, It.IsAny<CancellationToken>())).ReturnsAsync(new RosterRead(roster, "0:-1#42", "etag"));
        string encoded;
        using (var server = Server(first.Object))
        using (var client = server.GetTestClient())
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.Token());
            client.DefaultRequestHeaders.Add("X-Owner-Key", "attacker-owned-partition");
            var response = await client.GetAsync("/api/v1/participants");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl!.NoStore);
            encoded = response.Headers.GetValues(ReadSession.HeaderName).Single();
            Assert.Equal("0:-1#42", ReadSession.Decode(encoded));
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var people = json.RootElement.GetProperty("participants");
            Assert.Equal(new[] { "p002", "p001", "p003" }, people.EnumerateArray().Select(p => p.GetProperty("participantId").GetString()));
            Assert.All(people.EnumerateArray(), p => Assert.Equal(3, p.EnumerateObject().Count()));
            Assert.False(response.Headers.Contains("ETag"));
        }
        var second = new Mock<IRosterStore>(MockBehavior.Strict);
        second.Setup(s => s.ReadAsync(DataTests.Owner, "0:-1#42", It.IsAny<CancellationToken>())).ReturnsAsync(new RosterRead(roster, "0:-1#43", "etag"));
        using var nextServer = Server(second.Object);
        using var nextClient = nextServer.GetTestClient();
        nextClient.DefaultRequestHeaders.Authorization = new("Bearer", tokens.Token());
        nextClient.DefaultRequestHeaders.Add(ReadSession.HeaderName, encoded);
        var nextResponse = await nextClient.GetAsync("/api/v1/participants");
        Assert.Equal(HttpStatusCode.OK, nextResponse.StatusCode);
        Assert.Equal("0:-1#43", ReadSession.Decode(nextResponse.Headers.GetValues(ReadSession.HeaderName).Single()));
        first.VerifyAll(); second.VerifyAll();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Fewer_than_two_active_people_is_still_successful(int count)
    {
        var store = new Mock<IRosterStore>();
        store.Setup(s => s.ReadAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RosterRead(DataTests.Roster(Enumerable.Range(0, count).Select(i => DataTests.Person($"p{i}")).ToArray()), "0:-1#1", "etag"));
        using var server = Server(store.Object);
        using var client = server.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.Token());
        var response = await client.GetAsync("/api/v1/participants");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(count, json.RootElement.GetProperty("participants").GetArrayLength());
    }

    [Theory]
    [InlineData("missing", 401)]
    [InlineData("other-owner", 403)]
    [InlineData("session", 400)]
    [InlineData("query", 400)]
    [InlineData("body", 400)]
    public async Task Rejections_never_touch_the_database(string fault, int status)
    {
        var store = new Mock<IRosterStore>(MockBehavior.Strict);
        using var server = Server(store.Object);
        using var client = server.GetTestClient();
        if (fault != "missing") client.DefaultRequestHeaders.Authorization = new("Bearer",
            tokens.Token(d => { if (fault == "other-owner") d.Claims["oid"] = Guid.NewGuid().ToString(); }));
        if (fault == "session") client.DefaultRequestHeaders.Add(ReadSession.HeaderName, "not padded=");
        using var request = new HttpRequestMessage(HttpMethod.Get, fault == "query" ? "/api/v1/participants?ownerKey=other" : "/api/v1/participants");
        if (fault == "body") request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        var response = await client.SendAsync(request);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        store.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(503, "TEMPORARILY_UNAVAILABLE")]
    [InlineData(429, "RATE_LIMITED")]
    public async Task DB_failures_use_common_errors_and_retry_after(int status, string code)
    {
        var store = new Mock<IRosterStore>();
        store.Setup(s => s.ReadAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ApiException(status, code, "再試行してください。", 5));
        using var server = Server(store.Object);
        using var client = server.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.Token());
        var response = await client.GetAsync("/api/v1/participants");
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(5), response.Headers.RetryAfter!.Delta);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }
}
