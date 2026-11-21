using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services.Owner.Be4;

// Contract 6.2 step 8 (order 50): unsent pushes addressed to him go (bounded by ix_outbox_due, PublishedAt IS NULL), and his remaining
// calendar rows and festival marks are deleted explicitly (the FK cascades are the net). Returns no blocks.
public sealed class OwnerAccountDeletion(ApplicationDbContext db) : IAccountDeletionParticipant
{
    internal static readonly string UnsentPushesSql = $"""
        DELETE FROM "OutboxMessages"
        WHERE "PublishedAt" IS NULL AND "Exchange" = '{OutboxTargets.PushExchange}'
          AND (CASE WHEN "Exchange" = '{OutboxTargets.PushExchange}' THEN "Payload"::jsonb ->> '{OwnerNotifier.UserIdProperty}' END) = @user::text
        """;

    internal const string CalendarSql = """
        DELETE FROM "CalendarEntries" WHERE "OwnerUserId" = @user
        """;

    internal const string FestivalMarksSql = """
        DELETE FROM "FestivalReminderSent" WHERE "UserId" = @user
        """;

    public int Order => 50;

    public Task<IReadOnlyList<AccountDeletionBlock>> CheckAsync(Guid userId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AccountDeletionBlock>>([]);

    public async Task OnDeletingAsync(Guid userId, CancellationToken ct = default)
    {
        await db.Database.ExecuteSqlRawAsync(UnsentPushesSql, [User(userId)], ct);
        await db.Database.ExecuteSqlRawAsync(CalendarSql, [User(userId)], ct);
        await db.Database.ExecuteSqlRawAsync(FestivalMarksSql, [User(userId)], ct);
    }

    public Task AfterCommitAsync(Guid userId, CancellationToken ct = default) => Task.CompletedTask;

    private static NpgsqlParameter User(Guid userId) => new("user", NpgsqlDbType.Uuid) { Value = userId };
}
