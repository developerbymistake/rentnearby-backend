using System.Net;
using System.Text.Json;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Api.Middleware;

public class ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            var id = ShortId(context);
            if (ex is InvalidPhotoException)
                logger.LogWarning("Invalid photo {CorrelationId} {Method} {Route}", id, context.Request.Method, context.Request.Path.Value);
            else
                logger.LogError(ex, "Unhandled exception {CorrelationId} {Method} {Route}", id, context.Request.Method,
                    context.Request.Path.Value);
            if (context.Response.HasStarted) throw;
            await HandleExceptionAsync(context, ex, id);
        }
    }

    private static string ShortId(HttpContext context)
    {
        var id = context.TraceIdentifier ?? string.Empty;
        return id.Length > 8 ? id[^8..] : id;
    }

    private static Task HandleExceptionAsync(HttpContext context, Exception exception, string id)
    {
        var (code, type, message) = exception switch
        {
            // FB-27: framework text (parameter names, values) stays in the log; the body is a fixed line with the reference.
            InvalidPhotoException => (HttpStatusCode.BadRequest, "ValidationException", InvalidPhotoException.UserMessage),
            ArgumentNullException or ArgumentException => (HttpStatusCode.BadRequest, "ValidationException", $"Invalid request. Ref {id}"),
            UnauthorizedAccessException => (HttpStatusCode.Unauthorized, "UnauthorizedException", exception.Message),
            KeyNotFoundException or FileNotFoundException => (HttpStatusCode.NotFound, "NotFoundException", exception.Message),
            _ => (HttpStatusCode.InternalServerError, "InternalServerError",
                $"An error occurred while processing your request. Ref {id}")
        };
        context.Response.Headers["X-Correlation-Id"] = id;

        var response = new ApiResponse<object>
        {
            Status = ((int)code).ToString(),
            Code = (int)code,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Error = new ApiError { Message = message, Type = type }
        };

        context.Response.ContentType = "application/json";
        context.Response.StatusCode = (int)code;
        return context.Response.WriteAsync(JsonSerializer.Serialize(response));
    }
}
