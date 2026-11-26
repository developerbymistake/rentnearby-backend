using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.DTOs.Social;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Entities.Social;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Data;
using static RentNearBy.Infrastructure.Services.Social.SocialSql;

namespace RentNearBy.Infrastructure.Services.Social;

// Contract 2.7 / 2.8: trip post and message reports with a chat snapshot, the snapshot of a person report from a chat, and
// the admin reads. Reports go to the admin queue only (ListingReports, existing pending-unique index).
public sealed class SocialReportService(ApplicationDbContext db, PublicTripReader trips, IUserBlockService blocks, ILogger<SocialReportService> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly string ReporterSql = $"""
        SELECT rr."Name" AS "ReasonName", COALESCE(me."Name", '') AS "ReporterName", me."PhoneNumber" AS "ReporterMobile",
               COALESCE(t."Name", '') AS "TargetName", t."PhoneNumber" AS "TargetMobile", t."Username" AS "TargetUsername"
        FROM (SELECT 1) one
        LEFT JOIN "ReportReasons" rr ON rr."Id" = @reason AND rr."Scope" = '{ReportScopes.User}'
        LEFT JOIN "Users" me ON me."Id" = @me
        LEFT JOIN "Users" t ON t."Id" = @him
        """;

    // The reported message (any expiry): its sender comes from the row, never from the client.
    private static readonly string MessageSql = $"""
        SELECT m."SenderId" AS "Value" FROM "ChatMessages" m
        WHERE m."RoomId" = @room AND m."Seq" = @seq AND {SocialChatService.MemberOf("m.\"RoomId\"")}
        """;

    private static readonly string IsMemberSql = $"""SELECT {SocialChatService.MemberOf("@room")} AS "Value" """;

    private const string InsertReportSql = """
        INSERT INTO "ListingReports" ("Id", "ListingId", "ListingType", "ReporterUserId", "ReporterName", "ReporterMobile",
                                      "ReportedUserId", "ReportedName", "ReportedMobile", "ReasonId", "Details", "Status", "CreatedAt",
                                      "ReportedUsername", "Context", "MessageSeq")
        VALUES (gen_random_uuid(), @listing, @type, @me, @reporterName, @reporterMobile, @him, @targetName, @targetMobile, @reason,
                @details, 'Pending', now(), @username, NULL, @seq)
        ON CONFLICT ("ReporterUserId", "ListingId") WHERE "Status" = 'Pending' DO NOTHING
        RETURNING "Id" AS "Value"
        """;

    // The reported seq +-15 plus the newest 20, at most 50, ignoring ExpiresAt.
    private static readonly string SnapshotRowsSql = $"""
        SELECT x."Seq", x."SenderId", x."SenderName", x."Body", x."CreatedAt" FROM (
          SELECT m."Seq", m."SenderId", COALESCE(u."Name", '') AS "SenderName", m."Body", m."CreatedAt"
          FROM "ChatMessages" m LEFT JOIN "Users" u ON u."Id" = m."SenderId"
          WHERE m."RoomId" = @room AND (m."Seq" BETWEEN @anchor - {SocialLimits.SnapshotAround} AND @anchor + {SocialLimits.SnapshotAround}
                OR m."Seq" > (SELECT r."LastSeq" FROM "ChatRooms" r WHERE r."Id" = @room) - {SocialLimits.SnapshotNewest})
          ORDER BY m."Seq" DESC LIMIT {SocialLimits.SnapshotMax}) x
        ORDER BY x."Seq"
        """;

    private const string InsertSnapshotSql = """
        INSERT INTO "MessageSnapshots" ("Id", "ReportId", "RoomId", "TakenForUserId", "AboutUserId", "AnchorSeq", "MessagesJson", "CreatedAt", "PurgeAt")
        VALUES (gen_random_uuid(), @report, @room, @me, @him, @anchor, @json::jsonb, now(), now() + interval '90 days')
        """;

    public async Task<BuddyResult<SocialReportedDto>> ReportAsync(Guid me, SocialReportRequest r, CancellationToken ct = default)
    {
        Guid him, listing;
        long? seq = null;
        if (r.Scope == SocialReportTypes.TripPost)
        {
            if (r.TripId is not { } tripId || await trips.ByIdAsync(me, tripId, ct) is not { } post || post.IsMine)
                return BuddyResult<SocialReportedDto>.Fail(SocialErrorTypes.TripPostNotFound);
            him = post.Owner.UserId;
            listing = tripId;
        }
        else if (r.Scope == SocialReportTypes.Message)
        {
            if (r.RoomId is not { } roomId || r.Seq is not { } s) return BuddyResult<SocialReportedDto>.Fail(SocialErrorTypes.RoomNotFound);
            var member = (await db.Database.SqlQueryRaw<bool>(IsMemberSql, Uuid("me", me), Uuid("room", roomId)).ToListAsync(ct)).FirstOrDefault();
            if (!member) return BuddyResult<SocialReportedDto>.Fail(SocialErrorTypes.RoomNotFound);
            var sender = (await db.Database.SqlQueryRaw<Guid?>(MessageSql, Uuid("me", me), Uuid("room", roomId), Long("seq", s)).ToListAsync(ct)).FirstOrDefault();
            if (sender is not { } senderId || senderId == me) return BuddyResult<SocialReportedDto>.Fail(SocialErrorTypes.MessageGone);
            him = senderId;
            listing = roomId;
            seq = s;
        }
        else return BuddyResult<SocialReportedDto>.Fail(SocialErrorTypes.InvalidPost, new { field = "scope" });

        var info = (await db.Database.SqlQueryRaw<ReporterRow>(ReporterSql, Uuid("reason", r.ReasonId), Uuid("me", me), Uuid("him", him)).ToListAsync(ct))[0];
        if (info.ReasonName is null) return BuddyResult<SocialReportedDto>.Fail(BuddyErrorTypes.InvalidReason);

        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var reportId = (await db.Database.SqlQueryRaw<Guid>(InsertReportSql,
                Uuid("listing", listing), Text("type", r.Scope), Uuid("me", me), Text("reporterName", info.ReporterName),
                Text("reporterMobile", info.ReporterMobile ?? string.Empty), Uuid("him", him), Text("targetName", info.TargetName),
                Text("targetMobile", info.TargetMobile ?? string.Empty), Uuid("reason", r.ReasonId),
                Text("details", BuddyRules.ReportDetails(r.ReasonId, r.Note)), NullText("username", info.TargetUsername),
                new Npgsql.NpgsqlParameter("seq", NpgsqlTypes.NpgsqlDbType.Bigint) { Value = (object?)seq ?? DBNull.Value }).ToListAsync(ct)).FirstOrDefault();
            if (reportId != Guid.Empty && seq is { } anchor) await SnapshotAsync(reportId, listing, me, him, anchor, ct);
            await tx.CommitAsync(ct);
        }

        var blocked = false;
        if (r.AlsoBlock)
        {
            var outcome = await blocks.BlockAsync(me, him, ct);
            blocked = outcome.Result == BlockResult.Done;
        }
        return BuddyResult<SocialReportedDto>.Success(new SocialReportedDto { Reported = true, Blocked = blocked });
    }

    // POST /users/{id}/report with context Chat: the newest 50 messages of their direct room go with the pending report.
    public async Task AttachChatSnapshotAsync(Guid me, Guid him, CancellationToken ct = default)
    {
        var room = (await db.Database.SqlQueryRaw<Guid>($"""
            SELECT r."Id" AS "Value" FROM "ChatRooms" r
            WHERE r."Kind" = '{ChatKinds.Direct}' AND r."PairLowId" = LEAST(@me, @him) AND r."PairHighId" = GREATEST(@me, @him)
              AND {SocialChatService.MemberOf("r.\"Id\"")}
            """, Uuid("me", me), Uuid("him", him)).ToListAsync(ct)).FirstOrDefault();
        if (room == Guid.Empty) return;
        var reportId = await db.ListingReports.AsNoTracking()
            .Where(x => x.ReporterUserId == me && x.ListingId == him && x.Status == "Pending" && x.ListingType == ReportScopes.User)
            .Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
        if (reportId is not { } id) return;
        if (await db.Set<MessageSnapshot>().AnyAsync(s => s.ReportId == id, ct)) return;
        var last = await db.Set<ChatRoom>().Where(x => x.Id == room).Select(x => x.LastSeq).FirstAsync(ct);
        await SnapshotAsync(id, room, me, him, last, ct, anchored: false);
    }

    private async Task SnapshotAsync(Guid reportId, Guid roomId, Guid me, Guid him, long anchor, CancellationToken ct, bool anchored = true)
    {
        var rows = await db.Database.SqlQueryRaw<SnapRow>(SnapshotRowsSql, Uuid("room", roomId), Long("anchor", anchor)).ToListAsync(ct);
        var json = JsonSerializer.Serialize(rows.Select(m => new
        {
            seq = m.Seq, senderId = m.SenderId, senderName = m.SenderName, text = m.Body, createdAt = Utc(m.CreatedAt),
        }), Json);
        await db.Database.ExecuteSqlRawAsync(InsertSnapshotSql,
        [
            Uuid("report", reportId), Uuid("room", roomId), Uuid("me", me), Uuid("him", him),
            new Npgsql.NpgsqlParameter("anchor", NpgsqlTypes.NpgsqlDbType.Bigint) { Value = anchored ? anchor : (object)DBNull.Value },
            Text("json", json),
        ], ct);
    }

    // GET /admin/reports/{id}/snapshot. null = none.
    public async Task<MessageSnapshotDto?> AdminSnapshotAsync(Guid adminId, Guid reportId, CancellationToken ct = default)
    {
        var s = await db.Set<MessageSnapshot>().AsNoTracking().FirstOrDefaultAsync(x => x.ReportId == reportId, ct);
        if (s is null) return null;
        var reportedSeq = await db.ListingReports.AsNoTracking().Where(x => x.Id == reportId).Select(x => x.MessageSeq).FirstOrDefaultAsync(ct);
        logger.LogInformation("admin.action {Action} {AdminId} {TargetId}", "ReportSnapshotViewed", adminId, reportId);
        var items = JsonSerializer.Deserialize<List<SnapItem>>(s.MessagesJson, Json) ?? [];
        return new MessageSnapshotDto
        {
            Id = s.Id, RoomId = s.RoomId, AnchorSeq = s.AnchorSeq, CreatedAt = Utc(s.CreatedAt),
            Messages = items.Select(m => new SnapshotMessageDto
            {
                Seq = m.Seq, SenderId = m.SenderId, SenderName = m.SenderName ?? string.Empty, Text = m.Text ?? string.Empty,
                CreatedAt = Utc(m.CreatedAt), IsReported = reportedSeq is { } rs && rs == m.Seq,
            }).ToList(),
        };
    }

    // Admin report rows: messageSeq, hasSnapshot and the trip post block (two batched reads per page).
    public async Task EnrichAdminAsync(IReadOnlyList<(ListingReport Report, AdminListingReportDto Dto)> rows, CancellationToken ct = default)
    {
        if (rows.Count == 0) return;
        var ids = rows.Select(r => r.Report.Id).ToList();
        var withSnapshot = (await db.Set<MessageSnapshot>().AsNoTracking()
            .Where(s => s.ReportId != null && ids.Contains(s.ReportId.Value)).Select(s => s.ReportId!.Value).ToListAsync(ct)).ToHashSet();
        var tripIds = rows.Where(r => r.Report.ListingType == SocialReportTypes.TripPost).Select(r => r.Report.ListingId).Distinct().ToList();
        var posts = await db.Trips.AsNoTracking().Where(t => tripIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Title, t.PostCityNames, t.PostText, t.IsPublic }).ToDictionaryAsync(t => t.Id, ct);
        foreach (var (report, dto) in rows)
        {
            dto.MessageSeq = report.MessageSeq;
            dto.HasSnapshot = withSnapshot.Contains(report.Id);
            if (report.ListingType == SocialReportTypes.TripPost && posts.TryGetValue(report.ListingId, out var p))
                dto.TripPost = new ReportTripPostDto { TripId = p.Id, Title = p.Title, CityNames = p.PostCityNames.ToList(), Text = p.PostText ?? string.Empty, IsPublic = p.IsPublic };
        }
    }

    private sealed class ReporterRow
    {
        public string? ReasonName { get; set; }
        public string ReporterName { get; set; } = string.Empty;
        public string? ReporterMobile { get; set; }
        public string TargetName { get; set; } = string.Empty;
        public string? TargetMobile { get; set; }
        public string? TargetUsername { get; set; }
    }

    private sealed class SnapRow
    {
        public long Seq { get; set; }
        public Guid? SenderId { get; set; }
        public string SenderName { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
    }

    private sealed class SnapItem
    {
        public long Seq { get; set; }
        public Guid? SenderId { get; set; }
        public string? SenderName { get; set; }
        public string? Text { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
