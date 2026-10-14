using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace RollAndPay.Api.Functions;

public sealed class HealthFunction
{
    [Function("Health")]
    public IActionResult Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v1/health")]
        HttpRequest request)
    {
        request.HttpContext.Response.Headers.CacheControl = "no-store";

        return new OkObjectResult(new { status = "ok" });
    }
}
