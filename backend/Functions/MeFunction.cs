using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using RollAndPay.Api.Http;

namespace RollAndPay.Api.Functions;

public sealed class MeFunction
{
    [Function("Me")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v1/me")] HttpRequest request)
    {
        // Read once even with an unknown Content-Length, so chunked GET bodies are rejected too.
        if (request.QueryString.HasValue ||
            await request.Body.ReadAsync(new byte[1], request.HttpContext.RequestAborted) != 0)
            throw new ApiException(400, "INVALID_REQUEST", "本文とクエリは指定できません。");

        return new OkObjectResult(new { authorized = true, role = "administrator" });
    }
}
