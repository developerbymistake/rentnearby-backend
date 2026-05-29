using System.Net;
using RentNearBy.Core.DTOs.Responses;

namespace RentNearBy.Api.Extensions;

public static class ApiResults
{
    private static long Timestamp() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public static IResult OkResponse<T>(T data) => Results.Ok(new ApiResponse<T>
    {
        Data = data, Status = "200", Code = 200, Timestamp = Timestamp()
    });

    public static IResult CreatedResponse<T>(T data, string location) => Results.Created(location, new ApiResponse<T>
    {
        Data = data, Status = "201", Code = 201, Timestamp = Timestamp()
    });

    public static IResult BadRequestResponse(string message, string type = "BadRequest") =>
        Results.BadRequest(new ApiResponse<object>
        {
            Status = "400", Code = 400, Timestamp = Timestamp(),
            Error = new ApiError { Message = message, Type = type }
        });

    public static IResult UnauthorizedResponse(string message = "Unauthorized") =>
        Results.Json(new ApiResponse<object>
        {
            Status = "401", Code = 401, Timestamp = Timestamp(),
            Error = new ApiError { Message = message, Type = "Unauthorized" }
        }, statusCode: 401);

    public static IResult ForbiddenResponse(string message = "Forbidden") =>
        Results.Json(new ApiResponse<object>
        {
            Status = "403", Code = 403, Timestamp = Timestamp(),
            Error = new ApiError { Message = message, Type = "Forbidden" }
        }, statusCode: 403);

    public static IResult NotFoundResponse(string message = "Resource not found") =>
        Results.Json(new ApiResponse<object>
        {
            Status = "404", Code = 404, Timestamp = Timestamp(),
            Error = new ApiError { Message = message, Type = "NotFound" }
        }, statusCode: 404);

    public static IResult ConflictResponse(string message, string type = "Conflict") =>
        Results.Json(new ApiResponse<object>
        {
            Status = "409", Code = 409, Timestamp = Timestamp(),
            Error = new ApiError { Message = message, Type = type }
        }, statusCode: 409);

    public static IResult NoContentResponse() => Results.NoContent();

    public static IResult TooManyRequestsResponse() =>
        Results.Json(new ApiResponse<object>
        {
            Status = "429", Code = 429, Timestamp = Timestamp(),
            Error = new ApiError { Message = "Too many attempts. Please try again later.", Type = "TooManyRequests" }
        }, statusCode: 429);

    public static IResult ServerErrorResponse(string message = "An unexpected error occurred. Please try again later.") =>
        Results.Json(new ApiResponse<object>
        {
            Status = "500", Code = 500, Timestamp = Timestamp(),
            Error = new ApiError { Message = "Internal server error. Please contact support if the problem persists.", Type = "InternalServerError" }
        }, statusCode: 500);
}
