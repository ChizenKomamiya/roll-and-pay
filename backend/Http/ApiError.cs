using Microsoft.AspNetCore.Http;

namespace RollAndPay.Api.Http;

public sealed class ApiException(int statusCode, string code, string message, int? retryAfterSeconds = null,
    ApiErrorDetail[]? details = null) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
    public int? RetryAfterSeconds { get; } = retryAfterSeconds;
    public ApiErrorDetail[] Details { get; } = details ?? [];
}

public sealed record ApiErrorDetail(string Field, string Reason,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? GameId = null);
public sealed record ApiError(string Code, string Message, ApiErrorDetail[] Details, string RequestId);
public sealed record ApiErrorResponse(ApiError Error);

public static class ApiErrors
{
    public static Task WriteAsync(HttpContext context, int status, string code, string message, ApiErrorDetail[]? details = null)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(
            new ApiErrorResponse(new(code, message, details ?? [], context.TraceIdentifier)), context.RequestAborted);
    }
}
