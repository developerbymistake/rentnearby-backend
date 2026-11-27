using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services.Owner.Quote;

// /plans/me (ARCHITECTURE 3.8): one PK read of the owner's counter. A counter from an older IST month reads 0 used.
public static class StayQuoteUsageReader
{
    public sealed record Usage(int Used, DateTime ResetsAt);

    public static async Task<Usage> ReadAsync(ApplicationDbContext db, Guid userId, DateTime nowUtc, CancellationToken ct = default)
    {
        var month = SocialMonth.Key(IstTime.TodayIst(nowUtc));
        var counter = await db.StayQuoteCounters.AsNoTracking()
            .Where(c => c.UserId == userId)
            .Select(c => new { c.MonthKey, c.Used })
            .FirstOrDefaultAsync(ct);
        return new Usage(UsedThisMonth(counter?.MonthKey, counter?.Used, month), SocialMonth.ResetsAtUtc(nowUtc));
    }

    public static int UsedThisMonth(int? counterMonth, int? used, int currentMonth)
        => counterMonth == currentMonth ? used ?? 0 : 0;
}
