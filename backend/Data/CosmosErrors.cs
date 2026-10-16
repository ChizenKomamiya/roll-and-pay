using System.Net;
using Microsoft.Azure.Cosmos;
using RollAndPay.Api.Http;

namespace RollAndPay.Api.Data;

public static class CosmosErrors
{
    public static ApiException Unavailable() => new(503, "TEMPORARILY_UNAVAILABLE",
        "一時的に利用できません。時間をおいて再試行してください。", 5);

    public static ApiException Translate(CosmosException exception, bool suppliedSession) => exception.StatusCode switch
    {
        HttpStatusCode.TooManyRequests => new(429, "RATE_LIMITED", "時間をおいて再試行してください。",
            (int)Math.Clamp(Math.Ceiling(exception.RetryAfter?.TotalSeconds ?? 1), 1, 3600)),
        HttpStatusCode.BadRequest when suppliedSession => ReadSession.Invalid(),
        _ => Unavailable()
    };
}
