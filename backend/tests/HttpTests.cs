using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Hosting;
using RollAndPay.Api.Authentication;
using RollAndPay.Api.Functions;
using RollAndPay.Api.Http;
using Xunit;

namespace RollAndPay.Api.Tests;

public sealed class HttpTests : IClassFixture<TokenFixture>
{
    private readonly TokenFixture tokens;
    public HttpTests(TokenFixture tokens) => this.tokens = tokens;

    private IHost Server(Action? onBusiness = null, bool configured = true)
    {
        var validator = configured ? tokens.Validator() : new EntraTokenValidator(new ConfigurationBuilder().Build());
        var pipeline = new ApiHttpPipeline(validator, NullLogger<ApiHttpPipeline>.Instance);
        return new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(s => s.AddMvc()).Configure(app => app.Run(context =>
            pipeline.InvokeAsync(context, context.Request.Path == "/api/v1/health", async () =>
            {
                IActionResult result;
                if (context.Request.Path == "/api/v1/health") result = new HealthFunction().Run(context.Request);
                else
                {
                    onBusiness?.Invoke();
                    if (context.Request.Path == "/api/v1/throws") throw new Exception("secret-token-personal-name");
                    result = await new MeFunction().Run(context.Request);
                }
                await result.ExecuteResultAsync(new ActionContext(context, new RouteData(), new ActionDescriptor()));
            })))).Start();
    }

    [Fact]
    public async Task Me_returns_only_authorization_and_no_store()
    {
        using var server = Server();
        using var client = server.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.Token());
        var response = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"authorized\":true,\"role\":\"administrator\"}", await response.Content.ReadAsStringAsync());
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal(ApiHttpPipeline.ExposedHeaders, string.Join(", ", response.Headers.GetValues("Access-Control-Expose-Headers")));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(response.Headers.Contains("X-Read-Session"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Basic abc")]
    [InlineData("Bearer")]
    [InlineData("Bearer not.a.jwt")]
    [InlineData("Bearer token another")]
    public async Task Unauthenticated_requests_never_invoke_business_or_database(string? authorization)
    {
        var invoked = 0;
        using var server = Server(() => invoked++);
        using var client = server.GetTestClient();
        if (authorization is not null) client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authorization);
        // A new business endpoint is protected without needing to opt into authentication.
        var response = await client.GetAsync("/api/v1/future-business?invalid=true");
        await AssertError(response, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
        Assert.Equal(0, invoked);
    }

    [Fact]
    public async Task Forbidden_precedes_request_validation_and_does_not_invoke_business()
    {
        var invoked = 0;
        using var server = Server(() => invoked++);
        using var client = server.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.Token(d => d.Claims["oid"] = Guid.NewGuid().ToString()));
        var response = await client.GetAsync("/api/v1/me?bad=true");
        await AssertError(response, HttpStatusCode.Forbidden, "FORBIDDEN");
        Assert.Equal(0, invoked);
    }

    [Theory]
    [InlineData("expired", HttpStatusCode.Unauthorized, "UNAUTHENTICATED")]
    [InlineData("scope", HttpStatusCode.Forbidden, "FORBIDDEN")]
    public async Task Expiry_and_scope_failures_stop_before_business(string fault, HttpStatusCode status, string code)
    {
        var invoked = 0;
        using var server = Server(() => invoked++);
        using var client = server.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.Token(d =>
        {
            if (fault == "expired") d.Expires = DateTime.UtcNow.AddMinutes(-2);
            else d.Claims.Remove("scp");
        }));
        await AssertError(await client.GetAsync("/api/v1/me"), status, code);
        Assert.Equal(0, invoked);
    }

    [Fact]
    public async Task Me_rejects_a_body_with_unknown_content_length()
    {
        using var server = Server();
        using var client = server.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.Token());
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me") { Content = new UnknownLengthContent() };
        request.Headers.TransferEncodingChunked = true;
        await AssertError(await client.SendAsync(request), HttpStatusCode.BadRequest, "INVALID_REQUEST");
    }

    private sealed class UnknownLengthContent : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync("{}"u8.ToArray()).AsTask();
    }

    [Fact]
    public async Task Multiple_authorization_values_are_rejected()
    {
        using var server = Server();
        using var client = server.GetTestClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", new[] { $"Bearer {tokens.Token()}", $"Bearer {tokens.Token()}" });
        await AssertError(await client.GetAsync("/api/v1/me"), HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Health_and_preflight_remain_available_without_auth_configuration(bool configured)
    {
        var invoked = 0;
        using var server = Server(() => invoked++, configured);
        using var client = server.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "invalid");
        var health = await client.GetAsync("/api/v1/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("{\"status\":\"ok\"}", await health.Content.ReadAsStringAsync());
        Assert.True(health.Headers.CacheControl!.NoStore);
        var options = await client.SendAsync(new HttpRequestMessage(HttpMethod.Options, "/api/v1/games/future"));
        Assert.Equal(HttpStatusCode.NoContent, options.StatusCode);
        Assert.Equal("", await options.Content.ReadAsStringAsync());
        Assert.Equal(ApiHttpPipeline.AllowedHeaders, string.Join(", ", options.Headers.GetValues("Access-Control-Allow-Headers")));
        Assert.Contains("DELETE", string.Join(", ", options.Headers.GetValues("Access-Control-Allow-Methods")));
        Assert.Equal(0, invoked);
    }

    [Fact]
    public async Task Missing_configuration_is_a_safe_retryable_error()
    {
        using var server = Server(configured: false);
        using var client = server.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.Token());
        var response = await client.GetAsync("/api/v1/me");
        await AssertError(response, HttpStatusCode.ServiceUnavailable, "TEMPORARILY_UNAVAILABLE");
        Assert.Equal(TimeSpan.FromSeconds(5), response.Headers.RetryAfter!.Delta);
    }

    [Theory]
    [InlineData("/api/v1/me?extra=1", null)]
    [InlineData("/api/v1/me", "{}")]
    public async Task Me_rejects_body_and_query(string path, string? body)
    {
        using var server = Server();
        using var client = server.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.Token());
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        await AssertError(await client.SendAsync(request), HttpStatusCode.BadRequest, "INVALID_REQUEST");
    }

    [Fact]
    public async Task Unexpected_error_hides_internal_details_and_request_ids_are_server_generated()
    {
        using var server = Server();
        using var client = server.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.Token());
        client.DefaultRequestHeaders.Add("X-Request-ID", "untrusted-client-value");
        var first = await AssertError(await client.GetAsync("/api/v1/throws"), HttpStatusCode.InternalServerError, "INTERNAL_ERROR");
        var second = await AssertError(await client.GetAsync("/api/v1/throws"), HttpStatusCode.InternalServerError, "INTERNAL_ERROR");
        Assert.NotEqual(first, second);
    }

    private static async Task<string> AssertError(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret-token-personal-name", text);
        using var document = JsonDocument.Parse(text);
        var error = document.RootElement.GetProperty("error");
        Assert.Equal(code, error.GetProperty("code").GetString());
        Assert.Equal(0, error.GetProperty("details").GetArrayLength());
        Assert.True(Guid.TryParse(error.GetProperty("requestId").GetString(), out _));
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));
        return error.GetProperty("requestId").GetString()!;
    }
}
