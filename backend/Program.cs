using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RollAndPay.Api.Authentication;
using RollAndPay.Api.Http;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();
builder.Services.AddSingleton<EntraTokenValidator>();
builder.Services.AddSingleton<ApiHttpPipeline>();
builder.UseMiddleware<ApiHttpMiddleware>();

await builder.Build().RunAsync();
