using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Api.Middleware;
using RentNearBy.Core.DTOs.Responses;

namespace RentNearBy.Tests.Api;

public class ErrorHandlingMiddlewareTests
{
    private const string GenericMessage = "An error occurred while processing your request.";

    private static async Task<(HttpContext Context, ApiResponse<object> Body)> RunAsync(Exception exception)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var middleware = new ErrorHandlingMiddleware(_ => throw exception, NullLogger<ErrorHandlingMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        var body = await JsonSerializer.DeserializeAsync<ApiResponse<object>>(context.Response.Body);
        Assert.NotNull(body);
        return (context, body);
    }

    public static TheoryData<Exception, int, string, string> Mappings => new()
    {
        { new ArgumentException("x"), 400, "ValidationException", "x" },
        { new ArgumentNullException("p", "missing"), 400, "ValidationException", "missing (Parameter 'p')" },
        { new UnauthorizedAccessException("nope"), 401, "UnauthorizedException", "nope" },
        { new KeyNotFoundException("gone"), 404, "NotFoundException", "gone" },
        { new FileNotFoundException("file"), 404, "NotFoundException", "file" },
        { new InvalidOperationException("secret"), 500, "InternalServerError", GenericMessage },
        { new Exception("secret"), 500, "InternalServerError", GenericMessage },
    };

    [Theory]
    [MemberData(nameof(Mappings))]
    public async Task InvokeAsync_Exception_MappedEnvelope(Exception exception, int status, string type, string message)
    {
        var (context, body) = await RunAsync(exception);

        Assert.Equal(status, context.Response.StatusCode);
        Assert.Equal("application/json", context.Response.ContentType);
        Assert.Equal(status.ToString(), body.Status);
        Assert.Equal(status, body.Code);
        Assert.NotNull(body.Error);
        Assert.Equal(type, body.Error.Type);
        Assert.Equal(message, body.Error.Message);
        Assert.Null(body.Data);
        Assert.True(body.Timestamp > 0);
    }

    [Fact]
    public async Task InvokeAsync_ServerError_DoesNotLeakMessage()
    {
        var (context, _) = await RunAsync(new InvalidOperationException("db password is hunter2"));

        context.Response.Body.Position = 0;
        var raw = await new StreamReader(context.Response.Body).ReadToEndAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("hunter2", raw);
    }

    [Fact]
    public async Task InvokeAsync_NoException_CallsNextOnceAndLeavesResponse()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var calls = 0;
        var middleware = new ErrorHandlingMiddleware(_ =>
        {
            calls++;
            return Task.CompletedTask;
        }, NullLogger<ErrorHandlingMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        Assert.Equal(1, calls);
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal(0, context.Response.Body.Length);
    }
}
