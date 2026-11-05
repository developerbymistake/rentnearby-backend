using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

// Raw INSERT .. ON CONFLICT so a duplicate never aborts the surrounding transaction or leaves a tracked row.
public class TripPushLogRepository(ApplicationDbContext context) : ITripPushLogRepository
{
    public async Task<bool> TryInsertAsync(TripPushLog row)
    {
        var id = row.Id == Guid.Empty ? Guid.NewGuid() : row.Id;
        var sentAt = row.SentAt == default ? DateTime.UtcNow : row.SentAt;
        var rows = await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "TripPushLogs" ("Id", "TripId", "TripEnquiryId", "DedupeKey", "Kind", "SentAt")
            VALUES ({id}, {row.TripId}, {row.TripEnquiryId}, {row.DedupeKey}, {row.Kind}, {sentAt})
            ON CONFLICT ("DedupeKey") DO NOTHING
            """);
        return rows > 0;
    }

    public async Task<int> PurgeOlderThanAsync(DateTime cutoffUtc)
        => await context.TripPushLogs.Where(p => p.SentAt < cutoffUtc).ExecuteDeleteAsync();

    public async Task<List<string>> GetKeysAsync(Guid enquiryId, string keyPrefix)
        => await context.TripPushLogs.AsNoTracking()
            .Where(p => p.TripEnquiryId == enquiryId && p.DedupeKey.StartsWith(keyPrefix))
            .Select(p => p.DedupeKey)
            .ToListAsync();
}
