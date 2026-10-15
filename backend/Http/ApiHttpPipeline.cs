using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using RollAndPay.Api.Authentication;

namespace RollAndPay.Api.Http;

public sealed class ApiHttpPipeline(EntraTokenValidator validator, ILogger<ApiHttpPipeline> logger)
{
    public const string ExposedHeaders = "ETag, Location, Idempotency-Replayed, Retry-After, X-Read-Session";
    public const string AllowedHeaders = "Authorization, Content-Type, Idempotency-Key, If-Match, X-Read-Session";

    public async Task InvokeAsync(HttpContext context, bool anonymousHealth, Func<Task> next)
    {
        context.TraceIdentifier = Guid.NewGuid().ToString("D");
        SetHeaders(context);
        // IActionResult is executed by the outer Functions HTTP bridge after the worker middleware returns.
        context.Response.OnStarting(() => { SetHeaders(context); return Task.CompletedTask; });
        try
        {
            if (HttpMethods.IsOptions(context.Request.Method))
            {
                context.Response.Headers.AccessControlAllowMethods = "GET, POST, DELETE, OPTIONS";
                context.Response.Headers.AccessControlAllowHeaders = AllowedHeaders;
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }

            if (!anonymousHealth)
            {
                var authorization = context.Request.Headers.Authorization;
                if (authorization.Count != 1 ||
                    !AuthenticationHeaderValue.TryParse(authorization[0], out var header) ||
                    !header.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(header.Parameter) || header.Parameter.Any(char.IsWhiteSpace))
                {
                    await RejectAsync(context, TokenStatus.Unauthenticated);
                    return;
                }

                var check = await validator.ValidateAsync(header.Parameter, context.RequestAborted);
                if (check.Status != TokenStatus.Authorized)
                {
                    await RejectAsync(context, check.Status);
                    return;
                }

                context.User = check.Principal!;
                ReadSession.Attach(context);
            }

            await next();
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (ApiException exception) when (!context.Response.HasStarted)
        {
            context.Response.Clear();
            SetHeaders(context);
            if (exception.RetryAfterSeconds is int seconds)
                context.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await ApiErrors.WriteAsync(context, exception.StatusCode, exception.Code, exception.Message);
        }
        catch (Exception) when (!context.Response.HasStarted)
        {
            // Never log exception messages/objects: downstream exceptions may contain personal data or tokens.
            logger.LogError("API request failed. RequestId: {RequestId}", context.TraceIdentifier);
            context.Response.Clear();
            SetHeaders(context);
            await ApiErrors.WriteAsync(context, 500, "INTERNAL_ERROR", "処理中にエラーが発生しました。");
        }
    }

    private static void SetHeaders(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.AccessControlExposeHeaders = ExposedHeaders;
        // Origin admission and Access-Control-Allow-Origin belong to the Functions host CORS configuration.
    }

    private static Task RejectAsync(HttpContext context, TokenStatus status)
    {
        if (status == TokenStatus.Unauthenticated)
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return ApiErrors.WriteAsync(context, 401, "UNAUTHENTICATED", "認証が必要です。");
        }

        if (status == TokenStatus.Forbidden)
            return ApiErrors.WriteAsync(context, 403, "FORBIDDEN", "利用権限がありません。");

        context.Response.Headers.RetryAfter = "5";
        return ApiErrors.WriteAsync(context, 503, "TEMPORARILY_UNAVAILABLE", "一時的に利用できません。時間をおいて再試行してください。");
    }
}
