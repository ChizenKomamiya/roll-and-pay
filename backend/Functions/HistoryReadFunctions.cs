using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using RollAndPay.Api.Data;
using RollAndPay.Api.Games;
using RollAndPay.Api.History;
using RollAndPay.Api.Http;

namespace RollAndPay.Api.Functions;

public sealed class HistoryReadFunctions(HistoryReader reader)
{
    [Function("HistoryList")]
    public async Task<IActionResult> List(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v1/games")] HttpRequest request)
    {
        await RejectBody(request);
        var query = HistoryQuery.Parse(request.Query);
        return new OkObjectResult(await reader.ListAsync(OwnerKey.FromPrincipal(request.HttpContext.User), query,
            ReadSession.Attach(request.HttpContext), request.HttpContext.RequestAborted));
    }

    [Function("HistoryDetail")]
    public async Task<IActionResult> Detail(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v1/games/{gameId}/history-detail")] HttpRequest request, string gameId)
    {
        await RejectBody(request);
        if (request.QueryString.HasValue || gameId.Length != 36 || !Guid.TryParseExact(gameId, "D", out var id) || id == Guid.Empty)
            throw new ApiException(400, "INVALID_REQUEST", "ゲームID・クエリの形式を確認してください。");
        var detail = await reader.DetailAsync(OwnerKey.FromPrincipal(request.HttpContext.User), id,
            ReadSession.Attach(request.HttpContext), request.HttpContext.RequestAborted);
        request.HttpContext.Response.Headers.ETag = GameEtag.Create(detail.GameId, detail.Revision);
        return new OkObjectResult(detail);
    }

    private static async Task RejectBody(HttpRequest request)
    {
        if (await request.Body.ReadAsync(new byte[1], request.HttpContext.RequestAborted) != 0)
            throw new ApiException(400, "INVALID_REQUEST", "本文は指定できません。");
    }
}
