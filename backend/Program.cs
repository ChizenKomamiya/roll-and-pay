using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RollAndPay.Api.Authentication;
using RollAndPay.Api.Http;
using Azure.Core;
using Azure.Identity;
using RollAndPay.Api.Data;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();
builder.Services.AddSingleton<EntraTokenValidator>();
builder.Services.AddSingleton<ApiHttpPipeline>();
builder.Services.AddSingleton<TokenCredential>(_ => builder.Environment.IsDevelopment()
    ? new AzureCliCredential() : new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned));
builder.Services.AddSingleton<CosmosConnection>();
builder.Services.AddSingleton<IRosterStore, CosmosRosterStore>();
builder.UseMiddleware<ApiHttpMiddleware>();

await builder.Build().RunAsync();
