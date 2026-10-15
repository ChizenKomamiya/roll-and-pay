using Microsoft.Extensions.Configuration;

namespace RollAndPay.Api.Authentication;

public sealed record AuthSettings(
    Guid TenantId, Guid ApiClientId, Guid AllowedSpaClientId, Guid AdministratorObjectId, string RequiredScope)
{
    public string Issuer => $"https://login.microsoftonline.com/{TenantId:D}/v2.0";
    public string MetadataAddress => $"{Issuer}/.well-known/openid-configuration";

    // Resolve lazily: an incomplete configuration must deny business requests while health remains usable.
    public static AuthSettings? Read(IConfiguration configuration)
    {
        if (!TryId("TenantId", out var tenant) || !TryId("ApiClientId", out var api) ||
            !TryId("AllowedSpaClientId", out var spa) || !TryId("AdministratorObjectId", out var admin))
            return null;

        var scope = configuration["Auth:RequiredScope"];
        if (string.IsNullOrWhiteSpace(scope) || scope.Any(char.IsWhiteSpace) || api == spa)
            return null;

        return new(tenant, api, spa, admin, scope);

        bool TryId(string name, out Guid value) =>
            Guid.TryParse(configuration[$"Auth:{name}"], out value) && value != Guid.Empty;
    }
}
