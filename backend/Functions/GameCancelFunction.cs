using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using RollAndPay.Api.Data;
using RollAndPay.Api.Games;
using RollAndPay.Api.Http;

namespace RollAndPay.Api.Functions;

public sealed class GameCancelFunction(GameCanceller canceller)
{
    [Function("CancelGame")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/games/{gameId}/cancel")] HttpRequest request, string gameId)
    {
        if (request.QueryString.HasValue || gameId.Length != 36 || !Guid.TryParseExact(gameId, "D", out var id) || id == Guid.Empty)
            throw new ApiException(400, "INVALID_REQUEST", "ゲームIDとクエリを確認してください。");
        var key = UpdateRequest.Key(request);
        var ifMatch = UpdateRequest.IfMatch(request);
        using var json = await UpdateRequest.Body(request);
        if (json.RootElement.ValueKind != JsonValueKind.Object || json.RootElement.EnumerateObject().Any())
            throw new ApiException(422, "VALIDATION_ERROR", "本文には空のオブジェクトを指定してください。");
        var result = await canceller.CancelAsync(OwnerKey.FromPrincipal(request.HttpContext.User), id, key,
            UpdateRequest.Hash("POST", $"/api/v1/games/{id:D}/cancel", json.RootElement, ifMatch), ifMatch,
            ReadSession.Attach(request.HttpContext), request.HttpContext.RequestAborted);
        request.HttpContext.Response.Headers.ETag = result.Command.ResponseEtag;
        request.HttpContext.Response.Headers["Idempotency-Replayed"] = result.Replayed ? "true" : "false";
        return new ObjectResult(result.Command.ResponseBody) { StatusCode = result.Command.ResponseStatus };
    }
}
