using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;

namespace RollAndPay.Api.Http;

public sealed class ApiHttpMiddleware(ApiHttpPipeline pipeline) : IFunctionsWorkerMiddleware
{
    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        if (!context.FunctionDefinition.InputBindings.Values.Any(binding => binding.Type == "httpTrigger"))
        {
            await next(context);
            return;
        }

        var http = context.GetHttpContext() ?? throw new InvalidOperationException("HTTP integration is required.");
        var completed = false;
        await pipeline.InvokeAsync(http,
            context.FunctionDefinition.Name == "Health" && HttpMethods.IsGet(http.Request.Method),
            async () => { await next(context); completed = true; });

        if (!completed) context.GetInvocationResult().Value = new EmptyResult();
    }
}
