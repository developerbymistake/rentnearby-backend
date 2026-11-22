using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services;

// D-14 / FB-23: suspend and an admin role change delete the user's device tokens in their transaction. The last pushes of
// that transaction (the ring end, "Role updated") take a copy of the tokens first, so they still reach the phone.
public static class PushTokenSnapshot
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static Task<List<string>> ValidTokensAsync(ApplicationDbContext db, Guid userId, CancellationToken ct = default)
        => db.DeviceTokens.AsNoTracking().Where(t => t.UserId == userId && t.IsValid).Select(t => t.Token).ToListAsync(ct);

    // Outbox push rows added in this unit (not yet saved) for this user whose key matches get the token copy.
    public static int Stamp(ApplicationDbContext db, Guid userId, IReadOnlyList<string> tokens, Func<string, bool> key)
    {
        if (tokens.Count == 0) return 0;
        var stamped = 0;
        foreach (var entry in db.ChangeTracker.Entries<OutboxMessage>().Where(e => e.State == EntityState.Added && key(e.Entity.Key)).ToList())
        {
            var push = JsonSerializer.Deserialize<OutboxPushPayload>(entry.Entity.Payload, Json);
            if (push is null || push.UserId != userId) continue;
            entry.Entity.Payload = JsonSerializer.Serialize(push with { TargetTokens = tokens }, Json);
            stamped++;
        }
        return stamped;
    }
}

// Delivery side: the token copy wins over the lookup (the lookup is empty once the tokens are deleted).
public static class PushTargets
{
    public static List<string> Tokens(OutboxPushPayload push, IEnumerable<string> lookedUp)
        => (push.TargetTokens is { Count: > 0 } copy ? copy : lookedUp).Distinct().Where(t => t != push.ExceptToken).ToList();
}
