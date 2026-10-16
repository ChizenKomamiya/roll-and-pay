using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using RollAndPay.Api.Data;

namespace RollAndPay.Seed;

public sealed record SeedOptions(CosmosSettings Cosmos, Guid TenantId, string OwnerKey, string ParticipantsFile, bool Apply)
{
    public static SeedOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var apply = false;
        string[] allowed = ["--endpoint", "--database", "--container", "--tenant-id", "--administrator-object-id", "--participants-file", "--confirm-owner"];
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--apply" && !apply) { apply = true; continue; }
            if (!allowed.Contains(args[i], StringComparer.Ordinal) || i + 1 == args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("Unknown or incomplete argument.");
            if (!values.TryAdd(args[i], args[i + 1])) throw new ArgumentException("Duplicate argument.");
            i++;
        }
        string Required(string key) => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value : throw new ArgumentException("Required arguments are missing.");
        if (!Guid.TryParse(Required("--tenant-id"), out var tenant) || tenant == Guid.Empty ||
            !Guid.TryParse(Required("--administrator-object-id"), out var admin) || admin == Guid.Empty)
            throw new ArgumentException("Tenant and administrator IDs must be nonempty GUIDs.");
        var owner = Api.Data.OwnerKey.FromIds(tenant, admin);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cosmos:Endpoint"] = Required("--endpoint"), ["Cosmos:DatabaseName"] = Required("--database"),
            ["Cosmos:ContainerName"] = Required("--container")
        }).Build();
        var cosmos = CosmosSettings.Read(configuration) ?? throw new ArgumentException("Invalid Cosmos target.");
        if (cosmos.Endpoint.IsLoopback) throw new ArgumentException("This deployment tool requires an Azure endpoint; use the test harness for the emulator.");
        if (apply && (!values.TryGetValue("--confirm-owner", out var confirmed) || confirmed != owner))
            throw new ArgumentException("--apply requires --confirm-owner with the exact canonical tenant:user owner key.");
        return new(cosmos, tenant, owner, Required("--participants-file"), apply);
    }

    public async Task<InitialData> ReadAsync(CancellationToken cancellationToken = default)
    {
        using var stream = new FileStream(ParticipantsFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 64 * 1024) throw new ArgumentException("Participant file must be at most 64 KiB.");
        var input = await JsonSerializer.DeserializeAsync<SeedInput>(stream, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            AllowDuplicateProperties = false
        }, cancellationToken);
        return InitialData.Build(OwnerKey, input?.Participants ?? throw new ArgumentException("Missing participants."),
            DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    }

    private sealed record SeedInput { public required RosterParticipant[] Participants { get; init; } }
}
