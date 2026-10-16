using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RollAndPay.Api.Authentication;
using RollAndPay.Api.Http;
using Azure.Core;
using Azure.Identity;
using RollAndPay.Api.Data;
using RollAndPay.Api.Games;
using RollAndPay.Api.History;
using RollAndPay.Api.Payments;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();
builder.Services.AddSingleton<EntraTokenValidator>();
builder.Services.AddSingleton<ApiHttpPipeline>();
builder.Services.AddSingleton<TokenCredential>(_ => builder.Environment.IsDevelopment()
    ? new AzureCliCredential() : new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned));
builder.Services.AddSingleton<CosmosConnection>();
builder.Services.AddSingleton<IRosterStore, CosmosRosterStore>();
builder.Services.AddSingleton<ICosmosPointReader, CosmosPointReader>();
builder.Services.AddSingleton<GameReader>();
builder.Services.AddSingleton<IHistoryStore, CosmosHistoryStore>();
builder.Services.AddSingleton<HistoryReader>();
builder.Services.AddSingleton<HistoryDeleter>();
builder.Services.AddSingleton<ICosmosBatchWriter, CosmosBatchWriter>();
builder.Services.AddSingleton<GameCreator>();
builder.Services.AddSingleton<IDie, SecureDie>();
builder.Services.AddSingleton<GameDice>();
builder.Services.AddSingleton<GameCanceller>();
builder.Services.AddSingleton<PaymentSaver>();
builder.Services.AddSingleton<LossTotalsReader>();
builder.UseMiddleware<ApiHttpMiddleware>();

await builder.Build().RunAsync();
