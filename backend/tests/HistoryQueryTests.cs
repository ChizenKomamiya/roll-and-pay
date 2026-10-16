using System.Text;
using Microsoft.AspNetCore.Http;
using RollAndPay.Api.History;
using RollAndPay.Api.Http;
using Xunit;

namespace RollAndPay.Api.Tests;

public sealed class HistoryQueryTests
{
    internal static HistoryQuery Parse(string query) => HistoryQuery.Parse(new DefaultHttpContext
        { Request = { QueryString = new QueryString(query) } }.Request.Query);
    internal static string Cursor(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public void Defaults_and_millisecond_boundary_round_trip()
    {
        Assert.Equal(new HistoryQuery(20, null), Parse(""));
        var boundary = new HistoryBoundary(GameFixture.Now.AddMilliseconds(123), GameFixture.Id);
        foreach (var limit in new[] { 1, 20, 50 })
            Assert.Equal(new HistoryQuery(limit, boundary), Parse($"?limit={limit}&cursor={HistoryQuery.Encode(boundary, limit)}"));
    }

    [Theory]
    [InlineData("?limit=0")]
    [InlineData("?limit=51")]
    [InlineData("?limit=-1")]
    [InlineData("?limit=1.0")]
    [InlineData("?limit=1e1")]
    [InlineData("?limit=%202")]
    [InlineData("?limit=%2B2")]
    [InlineData("?limit=")]
    [InlineData("?limit=1&limit=2")]
    [InlineData("?Limit=20")]
    [InlineData("?ownerKey=other")]
    public void Invalid_query_is_400(string query)
    {
        var error = Assert.Throws<ApiException>(() => Parse(query));
        Assert.Equal(400, error.StatusCode); Assert.Equal("INVALID_REQUEST", error.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("=")]
    [InlineData("a")]
    [InlineData("e31")]
    [InlineData("_w")]
    [InlineData("e30=")]
    public void Noncanonical_base64url_or_invalid_JSON_is_400(string cursor) =>
        Assert.Equal("INVALID_CURSOR", Assert.Throws<ApiException>(() => Parse($"?cursor={cursor}")).Code);

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"version\":1,\"createdAt\":\"2026-10-16T00:00:00.000Z\",\"gameId\":\"7c56e8d0-1a58-4c45-8ef3-6505ad5c0a01\",\"limit\":1}")]
    public void Missing_fields_and_limit_mismatch_are_400(string json) =>
        Assert.Equal("INVALID_CURSOR", Assert.Throws<ApiException>(() => Parse($"?cursor={Cursor(json)}")).Code);

    [Theory]
    [InlineData("version", "2")]
    [InlineData("version", "\"1\"")]
    [InlineData("version", "null")]
    [InlineData("limit", "\"20\"")]
    [InlineData("limit", "20.5")]
    [InlineData("createdAt", "\"2026-10-16T00:00:00Z\"")]
    [InlineData("createdAt", "\"2026-10-16T09:00:00.000+09:00\"")]
    [InlineData("createdAt", "\"2026-02-30T00:00:00.000Z\"")]
    [InlineData("createdAt", "42")]
    [InlineData("gameId", "\"00000000-0000-0000-0000-000000000000\"")]
    [InlineData("gameId", "\"7C56E8D0-1A58-4C45-8EF3-6505AD5C0A01\"")]
    [InlineData("gameId", "null")]
    public void Invalid_cursor_values_are_400(string field, string value)
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse("{\"version\":1,\"createdAt\":\"2026-10-16T00:00:00.000Z\",\"gameId\":\"7c56e8d0-1a58-4c45-8ef3-6505ad5c0a01\",\"limit\":20}")!;
        json[field] = System.Text.Json.Nodes.JsonNode.Parse(value);
        Assert.Equal("INVALID_CURSOR", Assert.Throws<ApiException>(() => Parse($"?cursor={Cursor(json.ToJsonString())}")).Code);
    }

    [Fact]
    public void Duplicate_unknown_overlong_and_multiple_cursors_are_rejected()
    {
        var valid = "{\"version\":1,\"createdAt\":\"2026-10-16T00:00:00.000Z\",\"gameId\":\"7c56e8d0-1a58-4c45-8ef3-6505ad5c0a01\",\"limit\":20";
        foreach (var value in new[] { Cursor(valid + ",\"version\":1}"), Cursor(valid + ",\"owner\":\"other\"}"), new string('A', 4097) })
            Assert.Equal("INVALID_CURSOR", Assert.Throws<ApiException>(() => Parse($"?cursor={value}")).Code);
        Assert.Equal(400, Assert.Throws<ApiException>(() => Parse("?cursor=e30&cursor=e30")).StatusCode);
    }
}
