using System.Security.Claims;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace RollAndPay.Api.Authentication;

public enum TokenStatus { Authorized, Unauthenticated, Forbidden, Unavailable }
public sealed record TokenCheck(TokenStatus Status, ClaimsPrincipal? Principal = null);

public sealed class EntraTokenValidator
{
    private readonly AuthSettings? settings;
    private readonly TokenValidationParameters? validation;
    private readonly JsonWebTokenHandler handler = new() { MapInboundClaims = false, MaximumTokenSizeInBytes = 16 * 1024 };

    public EntraTokenValidator(IConfiguration configuration)
        : this(AuthSettings.Read(configuration), null) { }

    // Tests supply signing metadata through DI, never through a runtime setting or an authentication bypass.
    public EntraTokenValidator(AuthSettings? settings, BaseConfigurationManager? configurationManager)
    {
        this.settings = settings;
        if (settings is null) return;

        configurationManager ??= new ConfigurationManager<OpenIdConnectConfiguration>(
            settings.MetadataAddress, new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever(new HttpClient { Timeout = TimeSpan.FromSeconds(10) }) { RequireHttps = true });

        validation = new TokenValidationParameters
        {
            ConfigurationManager = configurationManager,
            ValidateIssuer = true,
            ValidIssuer = settings.Issuer,
            ValidateAudience = true,
            ValidAudience = settings.ApiClientId.ToString("D"),
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    }

    public async Task<TokenCheck> ValidateAsync(string token, CancellationToken cancellationToken = default)
    {
        if (!handler.CanReadToken(token)) return new(TokenStatus.Unauthenticated);
        if (validation is null || settings is null) return new(TokenStatus.Unavailable);

        try
        {
            // The JWT handler can turn a discovery failure into a missing-signing-key error.
            // Fetch cached metadata first to distinguish an unavailable authority from a bad token.
            await validation.ConfigurationManager.GetBaseConfigurationAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return new(TokenStatus.Unavailable); }

        var result = await handler.ValidateTokenAsync(token, validation);
        if (!result.IsValid)
        {
            // Metadata failures are transient. Do not expose validation exceptions or token contents.
            return new(result.Exception is SecurityTokenException or ArgumentException
                ? TokenStatus.Unauthenticated : TokenStatus.Unavailable);
        }

        var principal = new ClaimsPrincipal(result.ClaimsIdentity);
        if (SingleClaim(principal, "ver") != "2.0") return new(TokenStatus.Unauthenticated);

        if (!IsId(principal, "tid", settings.TenantId) ||
            !IsId(principal, "oid", settings.AdministratorObjectId) ||
            !IsId(principal, "azp", settings.AllowedSpaClientId) ||
            SingleClaim(principal, "idtyp") == "app" ||
            !(SingleClaim(principal, "scp")?.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Contains(settings.RequiredScope, StringComparer.Ordinal) ?? false))
            return new(TokenStatus.Forbidden);

        return new(TokenStatus.Authorized, principal);
    }

    private static string? SingleClaim(ClaimsPrincipal principal, string name)
    {
        var claims = principal.FindAll(name).ToArray();
        return claims.Length == 1 ? claims[0].Value : null;
    }

    private static bool IsId(ClaimsPrincipal principal, string name, Guid expected) =>
        Guid.TryParse(SingleClaim(principal, name), out var actual) && actual == expected;
}
