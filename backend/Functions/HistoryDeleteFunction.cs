using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using RollAndPay.Api.Data;
using RollAndPay.Api.History;
using RollAndPay.Api.Http;

namespace RollAndPay.Api.Functions;

public sealed class HistoryDeleteFunction(HistoryDeleter deleter)
{
    [Function("DeleteHistory")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "v1/games/{gameId}")] HttpRequest request, string gameId)
    {
        if (request.QueryString.HasValue || gameId.Length != 36 || !Guid.TryParseExact(gameId, "D", out var id) || id == Guid.Empty)
            throw new ApiException(400, "INVALID_REQUEST", "ゲームIDとクエリを確認してください。");
        var key = UpdateRequest.Key(request);
        var ifMatch = UpdateRequest.IfMatch(request);
        if (request.ContentLength > 0 || await request.Body.ReadAsync(new byte[1], request.HttpContext.RequestAborted) != 0)
            throw new ApiException(400, "INVALID_REQUEST", "本文は指定できません。");
        var result = await deleter.DeleteAsync(OwnerKey.FromPrincipal(request.HttpContext.User), id, key,
            UpdateRequest.Hash("DELETE", $"/api/v1/games/{id:D}", null, ifMatch), ifMatch,
            ReadSession.Attach(request.HttpContext), request.HttpContext.RequestAborted);
        request.HttpContext.Response.Headers.ETag = result.Command.ResponseEtag;
        request.HttpContext.Response.Headers["Idempotency-Replayed"] = result.Replayed ? "true" : "false";
        return new ObjectResult(result.Command.ResponseBody) { StatusCode = result.Command.ResponseStatus };
    }
}
