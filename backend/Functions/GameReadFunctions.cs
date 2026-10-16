using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using RollAndPay.Api.Data;
using RollAndPay.Api.Games;
using RollAndPay.Api.Http;

namespace RollAndPay.Api.Functions;

public sealed record ActiveGameResponse(GameResponse? Game);

public sealed class GameReadFunctions(GameReader reader)
{
    [Function("ActiveGame")]
    public async Task<IActionResult> Active(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v1/games/active")] HttpRequest request)
    {
        await ValidateRequest(request);
        var game = await reader.GetActiveAsync(OwnerKey.FromPrincipal(request.HttpContext.User),
            ReadSession.Attach(request.HttpContext), request.HttpContext.RequestAborted);
        SetEtag(request, game);
        return new OkObjectResult(new ActiveGameResponse(game));
    }

    [Function("GameDetail")]
    public async Task<IActionResult> Detail(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v1/games/{gameId}")] HttpRequest request, string gameId)
    {
        await ValidateRequest(request);
        if (gameId.Length != 36 || !Guid.TryParseExact(gameId, "D", out var id) || id == Guid.Empty)
            throw new ApiException(400, "INVALID_REQUEST", "ゲームIDの形式を確認してください。");
        var game = await reader.GetAsync(OwnerKey.FromPrincipal(request.HttpContext.User), id,
            ReadSession.Attach(request.HttpContext), request.HttpContext.RequestAborted);
        SetEtag(request, game);
        return new OkObjectResult(game);
    }

    private static async Task ValidateRequest(HttpRequest request)
    {
        if (request.QueryString.HasValue || await request.Body.ReadAsync(new byte[1], request.HttpContext.RequestAborted) != 0)
            throw new ApiException(400, "INVALID_REQUEST", "本文とクエリは指定できません。");
    }

    private static void SetEtag(HttpRequest request, GameResponse? game)
    {
        if (game is not null) request.HttpContext.Response.Headers.ETag = GameEtag.Create(game.GameId, game.Revision);
    }
}
