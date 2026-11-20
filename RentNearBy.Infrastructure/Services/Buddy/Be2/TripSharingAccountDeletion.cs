using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Data;
using static RentNearBy.Infrastructure.Services.Buddy.Be2.Be2Sql;

namespace RentNearBy.Infrastructure.Services.Buddy.Be2;

// Order 30 (contract 3.3 matrix). No check of its own (money is BE4's guard). Inside the deletion transaction: every
// joined viewer of a trip the user owns gets TripDeleted + "{title} was deleted.", the other notices of those trips go.
// Set-based over all his trips; his own member rows and his trips go by FK cascade.
public sealed class TripSharingAccountDeletion(ApplicationDbContext db, INoticeWriter notices) : IAccountDeletionParticipant
{
    private const string ViewersSql = """
        SELECT m."UserId", m."TripId", t."Title"
        FROM "Trips" t JOIN "TripMembers" m ON m."TripId" = t."Id"
        WHERE t."OwnerUserId" = @user AND m."Status" = 'Joined'
        """;

    private const string DeleteNoticesSql = """
        DELETE FROM "UserNotices" n
        WHERE n."TripId" IN (SELECT t."Id" FROM "Trips" t WHERE t."OwnerUserId" = @user) AND n."Kind" <> 'TripDeleted'
        """;

    private bool _pushed;

    public int Order => 30;

    public Task<IReadOnlyList<AccountDeletionBlock>> CheckAsync(Guid userId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AccountDeletionBlock>>([]);

    public async Task OnDeletingAsync(Guid userId, CancellationToken ct = default)
    {
        var viewers = await db.Database.SqlQueryRaw<ViewerRow>(ViewersSql, Id("user", userId)).ToListAsync(ct);
        await db.Database.ExecuteSqlRawAsync(DeleteNoticesSql, [Id("user", userId)], ct);
        if (viewers.Count == 0) return;
        await notices.UpsertAsync(viewers.Select(v => new NoticeUpsert(v.UserId, NoticeKinds.TripDeleted, v.TripId, null)).ToList(), ct);
        await notices.PushAsync(viewers.Select(v => BuddyPushCopy.TripDeletedForViewer(v.UserId, v.TripId, null, v.Title)).ToList(), ct);
        _pushed = true;
    }

    public Task AfterCommitAsync(Guid userId, CancellationToken ct = default)
    {
        if (_pushed) notices.Wake();
        return Task.CompletedTask;
    }

    private sealed class ViewerRow
    {
        public Guid UserId { get; set; }
        public Guid TripId { get; set; }
        public string Title { get; set; } = string.Empty;
    }
}
