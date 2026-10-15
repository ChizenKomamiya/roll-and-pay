using Microsoft.Extensions.Configuration;

namespace RollAndPay.Api.Data;

public sealed record CosmosSettings(Uri Endpoint, string DatabaseName, string ContainerName)
{
    public static CosmosSettings? Read(IConfiguration configuration)
    {
        var database = configuration["Cosmos:DatabaseName"];
        var container = configuration["Cosmos:ContainerName"];
        if (!Uri.TryCreate(configuration["Cosmos:Endpoint"], UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps || endpoint.UserInfo.Length != 0 ||
            endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 || endpoint.AbsolutePath != "/" ||
            !ValidName(database) || !ValidName(container)) return null;
        return new(endpoint, database!, container!);
    }

    private static bool ValidName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 255 &&
        name == name.Trim() && !name.Any(c => char.IsControl(c) || "/\\?#".Contains(c));
}
