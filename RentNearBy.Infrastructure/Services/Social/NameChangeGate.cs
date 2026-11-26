using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities.Social;
using RentNearBy.Core.Interfaces.Social;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Data;
using static RentNearBy.Infrastructure.Services.Social.SocialSql;

namespace RentNearBy.Infrastructure.Services.Social;

public enum NameStamp { NoProfile, Stamped, TooSoon }

public sealed record NameStampResult(NameStamp Outcome, DateTime? NextAllowedAt);

// Spec 23.9: one conditional statement stamps NameChangedAt only when the last change is at least 14 days old (or there was
// none since onboarding), so two racing changes cannot both pass. Admin name changes never come here.
public static class NameChangeStamp
{
    private const string StampSql = """
        UPDATE "UserProfiles" SET "NameChangedAt" = @now, "UpdatedAt" = @now
        WHERE "UserId" = @me AND ("NameChangedAt" IS NULL OR "NameChangedAt" <= @cutoff)
        """;

    public static async Task<NameStampResult> TryAsync(ApplicationDbContext db, Guid me, DateTime nowUtc, CancellationToken ct)
    {
        var stamped = await db.Database.ExecuteSqlRawAsync(StampSql,
            [Uuid("me", me), At("now", nowUtc), At("cutoff", nowUtc.AddDays(-SocialLimits.NameChangeDays))], ct);
        if (stamped > 0) return new NameStampResult(NameStamp.Stamped, null);
        var last = await db.Set<UserProfile>().AsNoTracking().Where(p => p.UserId == me)
            .Select(p => new { p.NameChangedAt }).FirstOrDefaultAsync(ct);
        if (last is null) return new NameStampResult(NameStamp.NoProfile, null);
        return new NameStampResult(NameStamp.TooSoon, last.NameChangedAt?.AddDays(SocialLimits.NameChangeDays) ?? nowUtc);
    }
}

// The legacy PUT /users/profile/name path: the same rule as PUT /social/me for an account with a traveller profile.
public sealed class NameChangeGate(ApplicationDbContext db, TimeProvider clock) : INameChangeGate
{
    public async Task<DateTime?> TryStampAsync(Guid userId, CancellationToken ct = default)
    {
        var result = await NameChangeStamp.TryAsync(db, userId, clock.GetUtcNow().UtcDateTime, ct);
        return result.Outcome == NameStamp.TooSoon ? result.NextAllowedAt : null;
    }
}
