using System.Text.Json;
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
        IRabbitMqPublisher publisher)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
            return BadRequestResponse("Message is required");

        var msg = new TestBroadcastMessage
        {
            Title = string.IsNullOrWhiteSpace(request.Title) ? "Test Notification" : request.Title,
            Body = request.Message
        };

        await publisher.PublishAsync("test.broadcast", JsonSerializer.Serialize(msg));

        return OkResponse(new { queued = true });
    }
}
