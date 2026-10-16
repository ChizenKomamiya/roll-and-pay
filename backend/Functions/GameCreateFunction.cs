using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using RollAndPay.Api.Data;
using RollAndPay.Api.Games;
using RollAndPay.Api.Http;

namespace RollAndPay.Api.Functions;

public sealed class GameCreateFunction(GameCreator creator)
{
    [Function("CreateGame")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/games")] HttpRequest request)
    {
        if (request.QueryString.HasValue || request.Headers.ContainsKey("If-Match"))
            throw new ApiException(400, "INVALID_REQUEST", "クエリとIf-Matchは指定できません。");
        var key = UpdateRequest.Key(request);
        using var json = await UpdateRequest.Body(request);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 ||
            !root.TryGetProperty("participantIds", out var people) || people.ValueKind != JsonValueKind.Array ||
            people.GetArrayLength() is < 2 or > 6 || people.EnumerateArray().Any(p => p.ValueKind != JsonValueKind.String))
            throw GameCreator.InvalidParticipants();
        var ids = people.EnumerateArray().Select(p => p.GetString()!).ToArray();
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length ||
            ids.Any(id => id.Length is < 1 or > 40 || id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))))
            throw GameCreator.InvalidParticipants();
        var result = await creator.CreateAsync(OwnerKey.FromPrincipal(request.HttpContext.User), key,
            UpdateRequest.Hash("POST", "/api/v1/games", root, null), ids,
            ReadSession.Attach(request.HttpContext), request.HttpContext.RequestAborted);
        var response = request.HttpContext.Response;
        response.Headers.ETag = result.Command.ResponseEtag;
        response.Headers.Location = result.Command.ResponseLocation;
        response.Headers["Idempotency-Replayed"] = result.Replayed ? "true" : "false";
        return new ObjectResult(result.Command.ResponseBody) { StatusCode = result.Command.ResponseStatus };
    }
}
