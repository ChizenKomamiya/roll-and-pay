using Azure.Identity;
using Microsoft.Azure.Cosmos;
using RollAndPay.Api.Data;
using RollAndPay.Seed;

try
{
    var options = SeedOptions.Parse(args);
    var data = await options.ReadAsync();
    Console.WriteLine($"Target: {options.Cosmos.Endpoint} / {options.Cosmos.DatabaseName} / {options.Cosmos.ContainerName}");
    Console.WriteLine($"Owner: {options.OwnerKey}; participants: {data.Roster.Participants.Length}; documents: 3");
    if (!options.Apply)
    {
        Console.WriteLine("Validated locally. No connection or writes were made. Use --apply and --confirm-owner to register.");
        return 0;
    }
    using var client = new CosmosClient(options.Cosmos.Endpoint.ToString(),
        new AzureCliCredential(new AzureCliCredentialOptions { TenantId = options.TenantId.ToString() }), CosmosConnection.ClientOptions());
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    await new InitialDataWriter(client.GetContainer(options.Cosmos.DatabaseName, options.Cosmos.ContainerName)).CreateAsync(data, deadline.Token);
    Console.WriteLine("Created roster, control and lossTotals in one transaction.");
    return 0;
}
catch (InitialDataConflictException exception) { Console.Error.WriteLine(exception.Message); return 3; }
catch (InitialDataWriteException exception) { Console.Error.WriteLine(exception.Message); return 4; }
catch (Exception)
{
    // Never echo file contents, SDK diagnostics, credentials, or exception objects containing participant names.
    Console.Error.WriteLine("Registration did not complete. Verify arguments, participant data, login and data-plane permissions. After a timeout, inspect all three documents before retrying.");
    return 2;
}
