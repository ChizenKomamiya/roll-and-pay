using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using RollAndPay.Api.Data;
using RollAndPay.Api.Http;
using RollAndPay.Api.Payments;

namespace RollAndPay.Api.Functions;

public sealed class PaymentSaveFunction(PaymentSaver saver)
{
    [Function("SavePayment")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/games/{gameId}/payment")] HttpRequest request, string gameId)
    {
        if (request.QueryString.HasValue || gameId.Length != 36 || !Guid.TryParseExact(gameId, "D", out var id) || id == Guid.Empty)
            throw new ApiException(400, "INVALID_REQUEST", "ゲームIDとクエリを確認してください。");
        var key = UpdateRequest.Key(request);
        var ifMatch = UpdateRequest.IfMatch(request);
        using var json = await UpdateRequest.Body(request);
        var totalYen = PaymentRequest.Validate(json.RootElement);
        var result = await saver.SaveAsync(OwnerKey.FromPrincipal(request.HttpContext.User), id, key,
            UpdateRequest.Hash("POST", $"/api/v1/games/{id:D}/payment", json.RootElement, ifMatch), ifMatch, totalYen,
            ReadSession.Attach(request.HttpContext), request.HttpContext.RequestAborted);
        request.HttpContext.Response.Headers.ETag = result.Command.ResponseEtag;
        request.HttpContext.Response.Headers["Idempotency-Replayed"] = result.Replayed ? "true" : "false";
        return new ObjectResult(result.Command.ResponseBody) { StatusCode = result.Command.ResponseStatus };
    }
}
