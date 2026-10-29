using System.Text.Json;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.Interfaces;

namespace RentNearBy.Infrastructure.Services;

// Best-effort: a RabbitMQ outage must never fail the job post, so every publish error is swallowed.
public static class StayJobPostedPublisher
{
    public const string SeekerQueue = "stayjob.posted.seekers";
    public const string AdminQueue = "stayjob.posted.admin";

    public static async Task PublishAsync(IRabbitMqPublisher publisher, Guid jobId, ILogger? logger = null)
    {
        var json = JsonSerializer.Serialize(new StayJobPostedMessage { JobId = jobId });

        try
        {
            await publisher.PublishAsync(SeekerQueue, json);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to publish {Queue} for job {JobId}", SeekerQueue, jobId);
        }

        try
        {
            await publisher.PublishAsync(AdminQueue, json);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to publish {Queue} for job {JobId}", AdminQueue, jobId);
        }
    }
}
