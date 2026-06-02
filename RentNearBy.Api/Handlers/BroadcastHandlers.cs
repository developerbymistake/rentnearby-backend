using System.Text.Json;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Services;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

public static class BroadcastHandlers
{
    public record BroadcastRequest(string Title, string Body);

    public static async Task<IResult> SendBroadcast(
        BroadcastRequest request,
        IUnitOfWork unitOfWork,
        IRabbitMqPublisher publisher,
        ILoggerFactory loggerFactory)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
            return BadRequestResponse("Title is required");
        if (string.IsNullOrWhiteSpace(request.Body))
            return BadRequestResponse("Body is required");

        const int batchSize = 5000;
        var totalUsers = await unitOfWork.DeviceTokens.GetValidTokenUserCountAsync();

        if (totalUsers == 0)
            return OkResponse(new { queued = 0, totalUsers = 0 });

        var batches = (int)Math.Ceiling((double)totalUsers / batchSize);

        try
        {
            for (var i = 0; i < batches; i++)
            {
                var msg = new BroadcastMessage
                {
                    Title  = request.Title.Trim(),
                    Body   = request.Body.Trim(),
                    Offset = i * batchSize,
                    Limit  = batchSize,
                };
                await publisher.PublishAsync("broadcast.notification", JsonSerializer.Serialize(msg));
            }
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger("Broadcast").LogError(ex, "Broadcast publish failed");
            return ServerErrorResponse($"Failed to queue broadcast: {ex.Message}");
        }

        return OkResponse(new { queued = batches, totalUsers });
    }
}
