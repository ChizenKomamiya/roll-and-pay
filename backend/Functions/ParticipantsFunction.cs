using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using RollAndPay.Api.Data;
using RollAndPay.Api.Http;

namespace RollAndPay.Api.Functions;

public sealed record ParticipantResponse(string ParticipantId, string DisplayName, int SortOrder);
public sealed record ParticipantsResponse(ParticipantResponse[] Participants);

public sealed class ParticipantsFunction(IRosterStore store)
{
    [Function("Participants")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v1/participants")] HttpRequest request)
    {
        if (request.QueryString.HasValue || await request.Body.ReadAsync(new byte[1], request.HttpContext.RequestAborted) != 0)
            throw new ApiException(400, "INVALID_REQUEST", "本文とクエリは指定できません。");
        var ownerKey = OwnerKey.FromPrincipal(request.HttpContext.User);
        var session = ReadSession.Attach(request.HttpContext);
        var read = await store.ReadAsync(ownerKey, session.Token, request.HttpContext.RequestAborted);
        session.Accept(read.SessionToken);
        return new OkObjectResult(new ParticipantsResponse(read.Document.Participants.Where(p => p.IsActive)
            .OrderBy(p => p.SortOrder).ThenBy(p => p.ParticipantId, StringComparer.Ordinal)
            .Select(p => new ParticipantResponse(p.ParticipantId, p.DisplayName, p.SortOrder)).ToArray()));
    }
}
