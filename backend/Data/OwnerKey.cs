using System.Security.Claims;
using RollAndPay.Api.Http;

namespace RollAndPay.Api.Data;

public static class OwnerKey
{
    public static string FromPrincipal(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true || !ClaimId("tid", out var tenant) || !ClaimId("oid", out var user))
            throw new ApiException(403, "FORBIDDEN", "利用権限がありません。");
        return FromIds(tenant, user);

        bool ClaimId(string name, out Guid id)
        {
            var claims = principal.FindAll(name).ToArray();
            id = Guid.Empty;
            return claims.Length == 1 && Guid.TryParse(claims[0].Value, out id) && id != Guid.Empty;
        }
    }

    public static string FromIds(Guid tenant, Guid user) => tenant == Guid.Empty || user == Guid.Empty
        ? throw new ArgumentException("Tenant and user IDs must be nonempty GUIDs.") : $"{tenant:D}:{user:D}";
}
