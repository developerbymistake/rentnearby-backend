using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services;

// New job pushes (D-08, FB-08). Seekers: one outbox row per stay per IST day, written in the create transaction with the
// dedupe key stayjob:{stayId}:{yyyyMMdd} (ON CONFLICT DO NOTHING), so a post / close / post loop pushes at most once a day
// and a crash after commit still delivers. The relay publishes it to the default exchange with the queue name as the
// routing key. Admins: best effort per job after commit (a RabbitMQ outage never fails the post).
public static class StayJobPostedPublisher
{
    public const string SeekerQueue = "stayjob.posted.seekers";
    public const string AdminQueue = "stayjob.posted.admin";
    public const string DefaultExchange = "";

    public static string SeekerKey(Guid stayId, DateOnly todayIst) => $"stayjob:{stayId:N}:{todayIst:yyyyMMdd}";

    internal const string StageSql = """
        INSERT INTO "OutboxMessages" ("Key", "Exchange", "RoutingKey", "Payload", "CreatedAt", "NextAttemptAt")
        VALUES (@key, @exchange, @routingKey, @payload, now(), now())
        ON CONFLICT ("Key") DO NOTHING
        """;

    // Inside the create transaction. Returns true when this post is the stay's first broadcast of the IST day.
    public static async Task<bool> StageSeekerBroadcastAsync(ApplicationDbContext db, Guid stayId, Guid jobId, DateOnly todayIst, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(new StayJobPostedMessage { JobId = jobId });
        return await db.Database.ExecuteSqlRawAsync(StageSql,
            [
                new NpgsqlParameter("key", NpgsqlDbType.Varchar) { Value = SeekerKey(stayId, todayIst) },
                new NpgsqlParameter("exchange", NpgsqlDbType.Varchar) { Value = DefaultExchange },
                new NpgsqlParameter("routingKey", NpgsqlDbType.Varchar) { Value = SeekerQueue },
                new NpgsqlParameter("payload", NpgsqlDbType.Text) { Value = json },
            ], ct) == 1;
    }

    public static async Task PublishAdminAsync(IRabbitMqPublisher publisher, Guid jobId, ILogger? logger = null)
    {
        try
        {
            await publisher.PublishAsync(AdminQueue, JsonSerializer.Serialize(new StayJobPostedMessage { JobId = jobId }));
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to publish {Queue} for job {JobId}", AdminQueue, jobId);
        }
    }
}
