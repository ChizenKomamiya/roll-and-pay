using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using RollAndPay.Api.Data;
using RollAndPay.Api.Games;
using RollAndPay.Api.Http;

namespace RollAndPay.Api.Functions;

public sealed class GameDiceFunctions(GameDice dice)
{
    [Function("RollDice")]
    public Task<IActionResult> Roll([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/games/{gameId}/rolls")] HttpRequest request, string gameId) => Run(request, gameId, false);

    [Function("CreateRound")]
    public Task<IActionResult> NextRound([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/games/{gameId}/rounds")] HttpRequest request, string gameId) => Run(request, gameId, true);

    private async Task<IActionResult> Run(HttpRequest request, string gameId, bool nextRound)
    {
        if (gameId.Length != 36 || !Guid.TryParseExact(gameId, "D", out var id) || id == Guid.Empty || request.QueryString.HasValue)
            throw new ApiException(400, "INVALID_REQUEST", "ゲームIDとクエリを確認してください。");
        var key = UpdateRequest.Key(request);
        var ifMatch = UpdateRequest.IfMatch(request);
        using var json = await UpdateRequest.Body(request);
        var root = json.RootElement;
        var field = nextRound ? "previousRoundNumber" : "roundNumber";
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(field, out var number) || number.ValueKind != JsonValueKind.Number ||
            !number.TryGetInt32(out var round) || round < 1 || number.GetRawText().Any(c => !char.IsAsciiDigit(c))) throw Invalid(field);
        var properties = root.EnumerateObject().ToArray();
        DiceRequest input;
        if (nextRound)
        {
            if (properties.Length != 1) throw Invalid(field);
            input = new(round, "next");
        }
        else
        {
            if (properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length ||
                !root.TryGetProperty("mode", out var mode) || mode.ValueKind != JsonValueKind.String) throw Invalid("mode");
            var value = mode.GetString();
            if (value == "remaining" && properties.Length == 2) input = new(round, value);
            else if (value == "individual" && properties.Length == 3 && root.TryGetProperty("participantId", out var participant) &&
                participant.ValueKind == JsonValueKind.String && participant.GetString() is { Length: >= 1 and <= 40 } person &&
                person.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) input = new(round, value, person);
            else throw Invalid("participantId");
        }
        var path = $"/api/v1/games/{id:D}/{(nextRound ? "rounds" : "rolls")}";
        var result = await dice.ExecuteAsync(OwnerKey.FromPrincipal(request.HttpContext.User), id, key,
            UpdateRequest.Hash("POST", path, root, ifMatch), ifMatch, input, nextRound,
            ReadSession.Attach(request.HttpContext), request.HttpContext.RequestAborted);
        request.HttpContext.Response.Headers.ETag = result.Command.ResponseEtag;
        if (result.Command.ResponseLocation is { } location) request.HttpContext.Response.Headers.Location = location;
        request.HttpContext.Response.Headers["Idempotency-Replayed"] = result.Replayed ? "true" : "false";
        return new ObjectResult(result.Command.ResponseBody) { StatusCode = result.Command.ResponseStatus };
    }

    private static ApiException Invalid(string field) => new(422, "VALIDATION_ERROR", "入力値を確認してください。", details: [new(field, "INVALID_VALUE")]);
}
