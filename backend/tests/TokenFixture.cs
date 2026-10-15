using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using RollAndPay.Api.Authentication;

namespace RollAndPay.Api.Tests;

public sealed class TokenFixture : IDisposable
{
    public AuthSettings Settings { get; } = new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Guid.Parse("33333333-3333-3333-3333-333333333333"),
        Guid.Parse("44444444-4444-4444-4444-444444444444"), "access_as_user");
    private readonly RSA rsa = RSA.Create(2048);
    public RsaSecurityKey Key { get; }
    public TokenFixture() => Key = new(rsa) { KeyId = "test-key" };

    public string Token(Action<SecurityTokenDescriptor>? change = null)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Settings.Issuer,
            Audience = Settings.ApiClientId.ToString(),
            NotBefore = DateTime.UtcNow.AddMinutes(-5),
            Expires = DateTime.UtcNow.AddMinutes(10),
            IssuedAt = DateTime.UtcNow.AddMinutes(-5),
            SigningCredentials = new SigningCredentials(Key, SecurityAlgorithms.RsaSha256),
            Claims = new Dictionary<string, object>
            {
                ["ver"] = "2.0", ["tid"] = Settings.TenantId.ToString(),
                ["oid"] = Settings.AdministratorObjectId.ToString(),
                ["azp"] = Settings.AllowedSpaClientId.ToString(), ["scp"] = "other access_as_user"
            }
        };
        change?.Invoke(descriptor);
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    public OpenIdConnectConfiguration Configuration()
    {
        var configuration = new OpenIdConnectConfiguration { Issuer = Settings.Issuer };
        configuration.SigningKeys.Add(new RsaSecurityKey(rsa.ExportParameters(false)) { KeyId = Key.KeyId });
        return configuration;
    }

    public EntraTokenValidator Validator() => new(Settings,
        new Microsoft.IdentityModel.Protocols.StaticConfigurationManager<OpenIdConnectConfiguration>(Configuration()));

    public void Dispose() => rsa.Dispose();
}
