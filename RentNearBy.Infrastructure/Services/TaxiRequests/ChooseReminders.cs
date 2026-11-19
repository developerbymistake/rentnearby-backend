using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services.TaxiRequests;

// O7: the two "choose your driver" reminders of a request. Armed when the traveller first has an accepted driver to choose
// (the accept that moves Open -> Collecting, or a lapsed selection that leaves accepts), cancelled when a driver is selected,
// when no accepted driver is left and when the request ends (EndRequestAsync cancels every kind). Round = the request
// version at arming, so a later arming never collides with an earlier, cancelled one.
public static class ChooseReminders
{
    public const string NowAndVersionSql = """
        SELECT now() AS "Now", "Version" FROM "TaxiRequests" WHERE "Id" = @id
        """;

    public static async Task ArmAsync(IDueActionScheduler scheduler, ApplicationDbContext db, Guid requestId, DateOnly dateFrom, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<NowRow>(NowAndVersionSql, new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = requestId }).ToListAsync(ct);
        if (rows.Count == 0) return;
        await CancelAsync(scheduler, db, requestId, ct);
        var now = DateTime.SpecifyKind(rows[0].Now, DateTimeKind.Utc);
        foreach (var (kind, dueAt) in TaxiDispatchRules.ChooseReminderTimes(now, dateFrom))
            await scheduler.AddAtAsync(db, kind, requestId, requestId, (int)rows[0].Version, dueAt, ct);
    }

    public static Task CancelAsync(IDueActionScheduler scheduler, ApplicationDbContext db, Guid requestId, CancellationToken ct)
        => scheduler.CancelForRequestAsync(db, requestId, DueActionKinds.ChooseReminders, ct);

    private sealed class NowRow
    {
        public DateTime Now { get; set; }
        public long Version { get; set; }
    }
}
