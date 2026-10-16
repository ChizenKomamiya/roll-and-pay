using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using RollAndPay.Api.Data;
using RollAndPay.Api.Http;
using RollAndPay.Api.Payments;

namespace RollAndPay.Api.Functions;

public sealed class LossTotalsFunction(LossTotalsReader reader)
{
    [Function("GetLossTotals")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v1/statistics/losses")] HttpRequest request)
    {
        var probe = new byte[1];
        if (request.QueryString.HasValue || request.ContentLength > 0 ||
            await request.Body.ReadAsync(probe, request.HttpContext.RequestAborted) != 0)
            throw new ApiException(400, "INVALID_REQUEST", "本文とクエリは指定できません。");
        return new OkObjectResult(await reader.GetAsync(OwnerKey.FromPrincipal(request.HttpContext.User),
            ReadSession.Attach(request.HttpContext), request.HttpContext.RequestAborted));
    }
}
