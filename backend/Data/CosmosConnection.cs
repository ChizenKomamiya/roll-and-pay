using Azure.Core;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace RollAndPay.Api.Data;

public sealed class CosmosConnection : IDisposable
{
    private readonly Lazy<CosmosClient> client;
    private readonly CosmosSettings? settings;

    public CosmosConnection(IConfiguration configuration, TokenCredential credential)
    {
        settings = CosmosSettings.Read(configuration);
        client = new(() => new CosmosClient(settings!.Endpoint.ToString(), credential, ClientOptions()));
    }

    public Container Container => settings is null
        ? throw CosmosErrors.Unavailable()
        : client.Value.GetContainer(settings.DatabaseName, settings.ContainerName);

    public static CosmosClientOptions ClientOptions() => new()
    {
        UseSystemTextJsonSerializerWithOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict,
            Converters = { new UtcTimestampConverter() }
        },
        ConsistencyLevel = ConsistencyLevel.Session,
        ConnectionMode = ConnectionMode.Gateway,
        RequestTimeout = TimeSpan.FromSeconds(5),
        MaxRetryAttemptsOnRateLimitedRequests = 2,
        MaxRetryWaitTimeOnRateLimitedRequests = TimeSpan.FromSeconds(3),
        EnableContentResponseOnWrite = false
    };

    public void Dispose() { if (client.IsValueCreated) client.Value.Dispose(); }
}
