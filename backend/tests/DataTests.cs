using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Moq;
using RollAndPay.Api.Data;
using RollAndPay.Api.Http;
using Xunit;

namespace RollAndPay.Api.Tests;

public sealed class DataTests
{
    internal const string Owner = "11111111-1111-1111-1111-111111111111:44444444-4444-4444-4444-444444444444";
    internal static RosterParticipant Person(string id = "p001", string name = "参加者A", int order = 1, bool active = true) =>
        new() { ParticipantId = id, DisplayName = name, SortOrder = order, IsActive = active };
    internal static RosterDocument Roster(params RosterParticipant[] people) => new()
    {
        Id = "roster", Type = "roster", OwnerKey = Owner, SchemaVersion = 1,
        Participants = people, UpdatedAt = DateTimeOffset.Parse("2026-10-16T00:00:00Z")
    };
    internal static string Encode(string token) => Convert.ToBase64String(Encoding.UTF8.GetBytes(token)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public void Owner_is_canonical_and_comes_only_from_authenticated_claims()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new("tid", Owner.Split(':')[0].ToUpperInvariant()), new("oid", Owner.Split(':')[1].ToUpperInvariant()),
            new("email", "untrusted@example.test")], "test"));
        Assert.Equal(Owner, OwnerKey.FromPrincipal(principal));
        Assert.Throws<ApiException>(() => OwnerKey.FromPrincipal(new ClaimsPrincipal(new ClaimsIdentity(principal.Claims))));
        principal.Identities.Single().AddClaim(new("oid", Guid.NewGuid().ToString()));
        Assert.Throws<ApiException>(() => OwnerKey.FromPrincipal(principal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("abc=")]
    [InlineData("ab+c")]
    [InlineData("ab/c")]
    [InlineData(" abcd")]
    [InlineData("abcd,abcd")]
    [InlineData("_w")]
    [InlineData("Cg")]
    [InlineData("AB")]
    public void Invalid_read_sessions_are_400(string encoded)
    {
        var exception = Assert.Throws<ApiException>(() => ReadSession.Decode(encoded));
        Assert.Equal(400, exception.StatusCode);
        Assert.Equal("INVALID_READ_SESSION", exception.Code);
    }

    [Fact]
    public void Read_session_enforces_exact_8_KiB_limit_and_roundtrips()
    {
        var token = new string('a', 6144);
        var encoded = Encode(token);
        Assert.Equal(8192, encoded.Length);
        Assert.Equal(token, ReadSession.Decode(encoded));
        Assert.Throws<ApiException>(() => ReadSession.Decode(encoded + "A"));
        Assert.Equal("0:-1#42", ReadSession.Decode(Encode("0:-1#42")));
        var context = new DefaultHttpContext();
        var session = ReadSession.Attach(context);
        session.Accept("0:-1#43");
        Assert.Equal("0:-1#43", session.Token);
        Assert.Throws<ApiException>(() => session.Accept(new string('a', 6145)));
        Assert.Throws<ApiException>(() => session.Accept(null));
        Assert.Throws<ApiException>(() => session.Accept("\ud800"));
    }

    [Fact]
    public void Multiple_or_empty_session_headers_are_invalid()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[ReadSession.HeaderName] = new[] { Encode("0:-1#1"), Encode("0:-1#2") };
        Assert.Throws<ApiException>(() => ReadSession.Attach(context));
        context.Request.Headers[ReadSession.HeaderName] = "";
        Assert.Throws<ApiException>(() => ReadSession.Attach(context));
    }

    [Fact]
    public void Roster_validation_includes_inactive_people_and_all_boundaries()
    {
        Assert.True(RosterValidation.IsValid([]));
        Assert.True(RosterValidation.IsValid(Enumerable.Range(0, 100).Select(i => Person($"p{i}", new string('a', 50), 0)).ToArray()));
        Assert.False(RosterValidation.IsValid(Enumerable.Range(0, 101).Select(i => Person($"p{i}")).ToArray()));
        Assert.False(RosterValidation.IsValid([Person(), Person(active: false)]));
        Assert.False(RosterValidation.IsValid([Person(name: " ")]));
        Assert.False(RosterValidation.IsValid([Person(name: new string('a', 51))]));
        Assert.False(RosterValidation.IsValid([Person(order: -1)]));
        Assert.False(RosterValidation.IsValid([Person(id: "p\n")]));
        Assert.False(RosterValidation.IsValid([Person(id: new string('a', 41))]));
        Assert.True(RosterValidation.IsValid([Person(id: new string('a', 40))]));
    }

    [Fact]
    public async Task Point_read_is_partition_scoped_and_forwards_session_to_SDK()
    {
        var container = new Mock<Container>(MockBehavior.Strict);
        var response = new Mock<ItemResponse<RosterDocument>>();
        var headers = new Headers();
        headers.Set("x-ms-session-token", "0:-1#43");
        response.SetupGet(r => r.Headers).Returns(headers);
        response.SetupGet(r => r.Resource).Returns(Roster(Person()));
        response.SetupGet(r => r.ETag).Returns("internal-etag");
        container.Setup(c => c.ReadItemAsync<RosterDocument>("roster", new PartitionKey(Owner),
            It.Is<ItemRequestOptions>(o => o.SessionToken == "0:-1#42" && o.ConsistencyLevel == ConsistencyLevel.Session), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response.Object);
        var read = await new CosmosRosterStore(container.Object).ReadAsync(Owner, "0:-1#42", default);
        Assert.Equal("0:-1#43", read.SessionToken);
        Assert.Equal("internal-etag", read.Etag);
        container.VerifyAll();
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("schema")]
    [InlineData("type")]
    [InlineData("id")]
    [InlineData("participants")]
    public async Task Wrong_owner_or_corrupt_roster_is_unavailable(string fault)
    {
        var roster = Roster(Person());
        roster = fault switch
        {
            "owner" => roster with { OwnerKey = "other-owner" }, "schema" => roster with { SchemaVersion = 2 },
            "type" => roster with { Type = "game" }, "id" => roster with { Id = "other" },
            _ => roster with { Participants = [Person(), Person()] }
        };
        var response = new Mock<ItemResponse<RosterDocument>>();
        response.SetupGet(r => r.Resource).Returns(roster);
        var container = new Mock<Container>();
        container.Setup(c => c.ReadItemAsync<RosterDocument>(It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response.Object);
        var error = await Assert.ThrowsAsync<ApiException>(() => new CosmosRosterStore(container.Object).ReadAsync(Owner, null, default));
        Assert.Equal(503, error.StatusCode);
    }

    [Theory]
    [InlineData(404, 0, false, 503)]
    [InlineData(404, 1002, true, 503)]
    [InlineData(403, 0, false, 503)]
    [InlineData(429, 0, false, 429)]
    [InlineData(400, 0, true, 400)]
    [InlineData(400, 0, false, 503)]
    [InlineData(503, 0, true, 503)]
    public async Task Cosmos_errors_are_safe_and_do_not_fall_back_to_a_stale_read(int status, int substatus, bool session, int expected)
    {
        var container = new Mock<Container>();
        container.Setup(c => c.ReadItemAsync<RosterDocument>(It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new CosmosException("sensitive-diagnostics", (HttpStatusCode)status, substatus, "activity", 1));
        var error = await Assert.ThrowsAsync<ApiException>(() => new CosmosRosterStore(container.Object).ReadAsync(Owner, session ? "0:-1#42" : null, default));
        Assert.Equal(expected, error.StatusCode);
        Assert.DoesNotContain("sensitive", error.Message);
        if (expected is 503 or 429) Assert.True(error.RetryAfterSeconds >= 1);
        container.Verify(c => c.ReadItemAsync<RosterDocument>(It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Missing_configuration_never_creates_a_client_or_database()
    {
        using var connection = new CosmosConnection(new ConfigurationBuilder().Build(), Mock.Of<Azure.Core.TokenCredential>());
        var error = await Assert.ThrowsAsync<ApiException>(() => new CosmosRosterStore(connection).ReadAsync(Owner, null, default));
        Assert.Equal(503, error.StatusCode);
    }

    [Fact]
    public async Task SDK_timeouts_are_unavailable_but_caller_cancellation_is_preserved()
    {
        var container = new Mock<Container>();
        container.Setup(c => c.ReadItemAsync<RosterDocument>(It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        var store = new CosmosRosterStore(container.Object);
        var error = await Assert.ThrowsAsync<ApiException>(() => store.ReadAsync(Owner, null, default));
        Assert.Equal(503, error.StatusCode);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReadAsync(Owner, null, cancelled.Token));
    }

    [Theory]
    [InlineData("http://account.documents.azure.com")]
    [InlineData("https://user:password@account.documents.azure.com")]
    [InlineData("https://account.documents.azure.com/path")]
    [InlineData("https://account.documents.azure.com/?key=value")]
    public void Invalid_Cosmos_configuration_is_rejected(string endpoint)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cosmos:Endpoint"] = endpoint, ["Cosmos:DatabaseName"] = "rollpay", ["Cosmos:ContainerName"] = "app"
        }).Build();
        Assert.Null(CosmosSettings.Read(config));
    }

    [Fact]
    public void Persisted_document_names_and_required_fields_follow_the_DB_contract()
    {
        var options = CosmosConnection.ClientOptions().UseSystemTextJsonSerializerWithOptions;
        var json = JsonSerializer.Serialize(Roster(Person()), options);
        Assert.Contains("\"ownerKey\"", json);
        Assert.Contains("\"schemaVersion\":1", json);
        var restored = JsonSerializer.Deserialize<RosterDocument>(json, options)!;
        Assert.True(RosterValidation.IsValid(restored, Owner));
        Assert.Equal(Roster(Person()).Participants, restored.Participants);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RosterDocument>("{\"id\":\"roster\"}", options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RosterDocument>(json.Replace("2026-10-16T00:00:00.000Z", "not-a-date"), options));
    }
}
