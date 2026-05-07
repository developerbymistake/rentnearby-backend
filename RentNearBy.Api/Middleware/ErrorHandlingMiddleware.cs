using System.Net;
using System.Text.Json;
using RentNearBy.Core.DTOs.Responses;

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
            logger.LogError(ex, "Unhandled exception");
            await HandleExceptionAsync(context, ex);
        }
    }

    private static Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        var (code, type, message) = exception switch
        {
            ArgumentNullException or ArgumentException => (HttpStatusCode.BadRequest, "ValidationException", exception.Message),
            UnauthorizedAccessException => (HttpStatusCode.Unauthorized, "UnauthorizedException", exception.Message),
            KeyNotFoundException or FileNotFoundException => (HttpStatusCode.NotFound, "NotFoundException", exception.Message),
            _ => (HttpStatusCode.InternalServerError, "InternalServerError", $"{exception.GetType().Name}: {exception.Message} | {exception.InnerException?.Message}")
        };

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
