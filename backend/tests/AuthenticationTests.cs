using System.Security.Cryptography;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using RollAndPay.Api.Authentication;
using Xunit;

namespace RollAndPay.Api.Tests;

public sealed class AuthenticationTests : IClassFixture<TokenFixture>
{
    private readonly TokenFixture tokens;
    public AuthenticationTests(TokenFixture tokens) => this.tokens = tokens;

    [Fact]
    public async Task Valid_delegated_administrator_token_preserves_owner_claims()
    {
        var result = await tokens.Validator().ValidateAsync(tokens.Token());
        Assert.Equal(TokenStatus.Authorized, result.Status);
        Assert.Equal(tokens.Settings.AdministratorObjectId.ToString(), result.Principal!.FindFirst("oid")!.Value);
        Assert.True(result.Principal.Identity!.IsAuthenticated);
    }

    [Theory]
    [InlineData("aud")]
    [InlineData("iss")]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("unsigned")]
    [InlineData("ver")]
    [InlineData("algorithm")]
    [InlineData("signature")]
    [InlineData("no-expiry")]
    public async Task Invalid_tokens_are_unauthenticated(string fault)
    {
        using var otherRsa = RSA.Create(2048);
        var token = tokens.Token(d =>
        {
            switch (fault)
            {
                case "aud": d.Audience = "https://graph.microsoft.com"; break;
                case "iss": d.Issuer = "https://login.microsoftonline.com/other/v2.0"; break;
                case "expired": d.Expires = DateTime.UtcNow.AddMinutes(-2); break;
                case "future": d.NotBefore = DateTime.UtcNow.AddMinutes(2); break;
                case "unsigned": d.SigningCredentials = null; break;
                case "ver": d.Claims["ver"] = "1.0"; break;
                case "algorithm": d.SigningCredentials = new(tokens.Key, SecurityAlgorithms.RsaSha512); break;
                case "signature": d.SigningCredentials = new(new RsaSecurityKey(otherRsa) { KeyId = tokens.Key.KeyId }, SecurityAlgorithms.RsaSha256); break;
            }
        });
        if (fault == "no-expiry")
        {
            var handler = new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false };
            token = handler.CreateToken(new SecurityTokenDescriptor
            {
                Issuer = tokens.Settings.Issuer, Audience = tokens.Settings.ApiClientId.ToString(),
                SigningCredentials = new(tokens.Key, SecurityAlgorithms.RsaSha256)
            });
        }
        Assert.Equal(TokenStatus.Unauthenticated, (await tokens.Validator().ValidateAsync(token)).Status);
    }

    [Theory]
    [InlineData("tid", "55555555-5555-5555-5555-555555555555")]
    [InlineData("oid", "55555555-5555-5555-5555-555555555555")]
    [InlineData("azp", "55555555-5555-5555-5555-555555555555")]
    [InlineData("scp", "access_as_user_extra")]
    [InlineData("scp", "ACCESS_AS_USER")]
    [InlineData("scp", null)]
    [InlineData("oid", null)]
    [InlineData("azp", null)]
    [InlineData("idtyp", "app")]
    public async Task Valid_signature_with_insufficient_permissions_is_forbidden(string claim, string? value)
    {
        var token = tokens.Token(d => { if (value is null) d.Claims.Remove(claim); else d.Claims[claim] = value; });
        Assert.Equal(TokenStatus.Forbidden, (await tokens.Validator().ValidateAsync(token)).Status);
    }

    [Fact]
    public async Task Application_token_and_SPA_ID_token_are_rejected()
    {
        var appToken = tokens.Token(d => { d.Claims.Remove("scp"); d.Claims["roles"] = new[] { "administrator" }; });
        var idToken = tokens.Token(d => { d.Audience = tokens.Settings.AllowedSpaClientId.ToString(); d.Claims.Remove("scp"); });
        Assert.Equal(TokenStatus.Forbidden, (await tokens.Validator().ValidateAsync(appToken)).Status);
        Assert.Equal(TokenStatus.Unauthenticated, (await tokens.Validator().ValidateAsync(idToken)).Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not.a.jwt")]
    [InlineData("{\"oid\":\"administrator\"}")]
    public async Task Malformed_tokens_are_rejected(string token) =>
        Assert.Equal(TokenStatus.Unauthenticated, (await tokens.Validator().ValidateAsync(token)).Status);

    [Fact]
    public async Task Oversized_token_is_rejected() =>
        Assert.Equal(TokenStatus.Unauthenticated, (await tokens.Validator().ValidateAsync(new string('a', 17000))).Status);

    [Theory]
    [InlineData("TenantId")]
    [InlineData("ApiClientId")]
    [InlineData("AllowedSpaClientId")]
    [InlineData("AdministratorObjectId")]
    [InlineData("RequiredScope")]
    public async Task Missing_configuration_fails_closed(string missing)
    {
        var values = new Dictionary<string, string?>
        {
            ["Auth:TenantId"] = tokens.Settings.TenantId.ToString(),
            ["Auth:ApiClientId"] = tokens.Settings.ApiClientId.ToString(),
            ["Auth:AllowedSpaClientId"] = tokens.Settings.AllowedSpaClientId.ToString(),
            ["Auth:AdministratorObjectId"] = tokens.Settings.AdministratorObjectId.ToString(),
            ["Auth:RequiredScope"] = "access_as_user"
        };
        values.Remove($"Auth:{missing}");
        var validator = new EntraTokenValidator(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        Assert.Equal(TokenStatus.Unavailable, (await validator.ValidateAsync(tokens.Token())).Status);
    }

    [Fact]
    public async Task Signing_key_rollover_refreshes_metadata()
    {
        var old = new OpenIdConnectConfiguration { Issuer = tokens.Settings.Issuer };
        using var oldRsa = RSA.Create(2048);
        old.SigningKeys.Add(new RsaSecurityKey(oldRsa) { KeyId = "old" });
        var manager = new RotatingManager(old, tokens.Configuration());
        var validator = new EntraTokenValidator(tokens.Settings, manager);
        Assert.Equal(TokenStatus.Authorized, (await validator.ValidateAsync(tokens.Token())).Status);
        Assert.True(manager.Refreshed);
    }

    private sealed class RotatingManager(BaseConfiguration old, BaseConfiguration current) : BaseConfigurationManager
    {
        public bool Refreshed { get; private set; }
        public override Task<BaseConfiguration> GetBaseConfigurationAsync(CancellationToken cancel) => Task.FromResult(Refreshed ? current : old);
        public override void RequestRefresh() => Refreshed = true;
    }

    [Fact]
    public async Task Discovery_and_JWKS_are_retrieved_and_cached_by_the_real_configuration_manager()
    {
        var requests = 0;
        var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(tokens.Key.Rsa.ExportParameters(false)) { KeyId = tokens.Key.KeyId });
        using var transport = new MetadataTransport(request =>
        {
            requests++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("openid-configuration")
                    ? JsonSerializer.Serialize(new { issuer = tokens.Settings.Issuer, jwks_uri = "https://login.microsoftonline.com/test/keys" })
                    : JsonSerializer.Serialize(new { keys = new[] { new { kty = jwk.Kty, kid = jwk.Kid, n = jwk.N, e = jwk.E, use = "sig" } } }), Encoding.UTF8, "application/json")
            };
        });
        using var http = new HttpClient(transport);
        var manager = new ConfigurationManager<OpenIdConnectConfiguration>(tokens.Settings.MetadataAddress,
            new OpenIdConnectConfigurationRetriever(), new HttpDocumentRetriever(http) { RequireHttps = true });
        var validator = new EntraTokenValidator(tokens.Settings, manager);
        Assert.Equal(TokenStatus.Authorized, (await validator.ValidateAsync(tokens.Token())).Status);
        Assert.Equal(TokenStatus.Authorized, (await validator.ValidateAsync(tokens.Token())).Status);
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task Metadata_outage_is_unavailable_and_does_not_allow_business_access()
    {
        using var transport = new MetadataTransport(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var http = new HttpClient(transport);
        var manager = new ConfigurationManager<OpenIdConnectConfiguration>(tokens.Settings.MetadataAddress,
            new OpenIdConnectConfigurationRetriever(), new HttpDocumentRetriever(http));
        var result = await new EntraTokenValidator(tokens.Settings, manager).ValidateAsync(tokens.Token());
        Assert.Equal(TokenStatus.Unavailable, result.Status);
        Assert.Null(result.Principal);
    }

    private sealed class MetadataTransport(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
