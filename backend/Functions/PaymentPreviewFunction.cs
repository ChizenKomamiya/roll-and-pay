using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using RollAndPay.Api.Data;
using RollAndPay.Api.Games;
using RollAndPay.Api.Http;
using RollAndPay.Api.Payments;

namespace RollAndPay.Api.Functions;

public sealed record PaymentPreviewResponse(Guid GameId, long Revision, PaymentSnapshot Payment);

public sealed class PaymentPreviewFunction(GameReader reader)
{
    [Function("PaymentPreview")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/games/{gameId}/payment-preview")] HttpRequest request, string gameId)
    {
        if (gameId.Length != 36 || !Guid.TryParseExact(gameId, "D", out var id) || id == Guid.Empty)
            throw new ApiException(400, "INVALID_REQUEST", "ゲームIDの形式を確認してください。");
        var totalYen = await PaymentRequest.ReadAsync(request);
        var game = await reader.GetAsync(OwnerKey.FromPrincipal(request.HttpContext.User), id,
            ReadSession.Attach(request.HttpContext), request.HttpContext.RequestAborted);
        if (game.Status != "awaitingPayment")
            throw new ApiException(409, "INVALID_GAME_STATE", "敗者確定後、金額確定前のゲームだけプレビューできます。");
        var payment = PaymentCalculator.Calculate(totalYen, game.Participants, game.LoserParticipantId!);
        request.HttpContext.Response.Headers.ETag = GameEtag.Create(game.GameId, game.Revision);
        return new OkObjectResult(new PaymentPreviewResponse(game.GameId, game.Revision, payment));
    }
}
