using System.Text.Json;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Services;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

/// <summary>
/// TESTING ONLY — Remove this file and TestNotificationEndpoints.cs before production.
/// Also remove the route in AdminEndpoints.cs and the tab in admin Flutter main_screen.dart.
/// </summary>
public static class TestNotificationHandlers
{
    public record TestNotificationRequest(string Title, string Message);

    public static async Task<IResult> SendTestNotification(
        TestNotificationRequest request,
        IRabbitMqPublisher publisher,
        ILoggerFactory loggerFactory)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
            return BadRequestResponse("Message is required");

        var msg = new TestBroadcastMessage
        {
            Title = string.IsNullOrWhiteSpace(request.Title) ? "Test Notification" : request.Title,
            Body = request.Message
        };

        try
        {
            await publisher.PublishAsync("test.broadcast", JsonSerializer.Serialize(msg));
            return OkResponse(new { queued = true });
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger("TestNotification").LogError(ex, "Test notification publish failed");
            return ServerErrorResponse($"RabbitMQ error: {ex.Message}");
        }
    }
}
