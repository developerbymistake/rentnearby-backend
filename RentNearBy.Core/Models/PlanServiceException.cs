namespace RentNearBy.Core.Models;

// Expected business failure from the plan services. Handlers map StatusCode + ErrorType (PlanErrorTypes.*)
// onto ApiResults; Payload carries extra fields such as {limit, used}.
public sealed class PlanServiceException(
    int statusCode, string errorType, string message, object? payload = null, TimeSpan? retryAfter = null)
    : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string ErrorType { get; } = errorType;
    public object? Payload { get; } = payload;
    public TimeSpan? RetryAfter { get; } = retryAfter;

    public static PlanServiceException BadRequest(string type, string message, object? payload = null) => new(400, type, message, payload);
    public static PlanServiceException Forbidden(string type, string message) => new(403, type, message);
    public static PlanServiceException NotFound(string type, string message) => new(404, type, message);
    public static PlanServiceException Conflict(string type, string message, object? payload = null) => new(409, type, message, payload);
    public static PlanServiceException TooMany(TimeSpan? retryAfter) => new(429, "RateLimited", "Too many attempts. Please try again later.", null, retryAfter);
    public static PlanServiceException Gateway(string message) => new(503, PlanErrorTypes.GatewayError, message);
}
