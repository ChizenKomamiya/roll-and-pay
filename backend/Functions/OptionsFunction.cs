using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace RollAndPay.Api.Functions;

public sealed class OptionsFunction
{
    [Function("Options")]
    public IActionResult Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "options", Route = "v1/{*path}")] HttpRequest request)
        => new NoContentResult();
}
