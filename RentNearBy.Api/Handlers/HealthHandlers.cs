using Microsoft.EntityFrameworkCore;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Infrastructure.Services.TaxiRequests.DueActions;
using RentNearBy.Infrastructure.Services.TaxiRequests.Matching;

namespace RentNearBy.Api.Handlers;

// /health/live: the process answers, no IO. /health/ready: Postgres SELECT 1 (2 s) plus the age of each worker heartbeat; 200 or 503.
// The old GET /health stays as it is in Program.cs.
public static class HealthHandlers
{
    public static readonly TimeSpan DatabaseTimeout = TimeSpan.FromSeconds(2), NeverStartedGrace = TimeSpan.FromMinutes(2);

    public static readonly IReadOnlyList<(string Name, string Key, TimeSpan MaxAge)> Workers =
    [
        (WorkerNames.TripTimer, "tripTimer", TimeSpan.FromMinutes(11)),
        (WorkerNames.OutboxRelay, "outboxRelay", TimeSpan.FromSeconds(30)),
        (WorkerNames.PushDelivery, "pushDelivery", TimeSpan.FromSeconds(60)),
        (DueActionsWorker.HeartbeatName, "dueActions", TimeSpan.FromSeconds(30)),
        (TaxiMatcherService.HeartbeatName, "matcher", TimeSpan.FromSeconds(30)),
    ];

    public static IResult Live() => Results.Ok(new { status = "live" });

    public static Task<IResult> Ready(IServiceProvider sp, WorkerHeartbeats heartbeats, CancellationToken ct)
        => ReadyAsync(async probeCt =>
        {
            using var scope = sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Database.ExecuteSqlRawAsync("SELECT 1", probeCt);
        }, heartbeats, ct);

    // The database probe is injected so the verdict is testable without a database.
    public static async Task<IResult> ReadyAsync(Func<CancellationToken, Task> databaseProbe, WorkerHeartbeats heartbeats, CancellationToken ct)
    {
        var dbOk = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(DatabaseTimeout);
            await databaseProbe(timeout.Token);
            dbOk = true;
        }
        catch (Exception)
        {
        }

        var checks = new Dictionary<string, object> { ["database"] = new { ok = dbOk } };
        var healthy = dbOk;
        foreach (var (name, key, maxAge) in Workers)
        {
            var age = heartbeats.AgeOf(name);
            var ok = age is { } a ? a <= maxAge : heartbeats.Uptime <= NeverStartedGrace;
            healthy &= ok;
            checks[key] = new { ok, ageSeconds = age is { } v ? (int?)Math.Round(v.TotalSeconds) : null, maxSeconds = (int)maxAge.TotalSeconds };
        }

        return Results.Json(
            new { status = healthy ? "ready" : "unavailable", checks },
            statusCode: healthy ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
    }
}
