using System.Net;
using Microsoft.Azure.Cosmos;

namespace RollAndPay.Seed;

public sealed class InitialDataConflictException() : Exception("Initial data already exists. No documents were overwritten.");
public sealed class InitialDataWriteException() : Exception("Initial registration failed. Check the target and permissions; after a timeout, inspect all three documents before retrying.");

public sealed class InitialDataWriter(Container container)
{
    public async Task CreateAsync(InitialData data, CancellationToken cancellationToken = default)
    {
        // Construct all three documents from the validated roster; callers cannot inject a different partition or existing totals.
        var initial = InitialData.Build(data.Roster.OwnerKey, data.Roster.Participants, data.Roster.UpdatedAt);
        try
        {
            using var response = await container.CreateTransactionalBatch(new PartitionKey(initial.Roster.OwnerKey))
                .CreateItem(initial.Roster).CreateItem(initial.Control).CreateItem(initial.LossTotals)
                .ExecuteAsync(cancellationToken);
            if (response.IsSuccessStatusCode) return;
            if (response.StatusCode == HttpStatusCode.Conflict || response.Any(r => r.StatusCode == HttpStatusCode.Conflict))
                throw new InitialDataConflictException();
            throw new InitialDataWriteException();
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.Conflict) { throw new InitialDataConflictException(); }
        catch (CosmosException) { throw new InitialDataWriteException(); }
    }
}
