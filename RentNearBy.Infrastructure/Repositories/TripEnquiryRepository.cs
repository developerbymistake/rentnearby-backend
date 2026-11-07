using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class TripEnquiryRepository(ApplicationDbContext context) : Repository<TripEnquiry>(context), ITripEnquiryRepository
{
    public async Task<TripEnquiry?> GetByIdTrackedAsync(Guid id)
        => await _dbSet.FirstOrDefaultAsync(e => e.Id == id);

    public async Task<TripEnquiry?> GetByTokenHashAsync(string hash)
        => await _dbSet.FirstOrDefaultAsync(e => e.HostConfirmTokenHash == hash);

    public async Task<IReadOnlyList<TripEnquiry>> GetByTripAsync(Guid tripId)
        => await _dbSet.AsNoTracking()
            .Where(e => e.TripId == tripId)
            .OrderByDescending(e => e.CreatedAt)
            .ThenByDescending(e => e.Id)
            .ToListAsync();

    public async Task<IReadOnlyList<TripEnquiry>> GetByTripsAsync(IReadOnlyCollection<Guid> tripIds)
    {
        if (tripIds.Count == 0) return [];
        var ids = tripIds.Distinct().ToList();
        return await _dbSet.AsNoTracking()
            .Where(e => e.TripId != null && ids.Contains(e.TripId.Value))
            .OrderByDescending(e => e.CreatedAt)
            .ThenByDescending(e => e.Id)
            .ToListAsync();
    }

    // Due work queues: only rows whose reminder key is not logged yet (the set shrinks as pushes go out), keyset by Id
    // so a caller paging to the end reaches every due row. Key expressions mirror TripReminderRules.NoReplyKey /
    // NoReplySegmentKey / HostNotConfirmedKey (Guid "D" = uuid::text, Guid "N" = hyphens removed).
    public async Task<(IReadOnlyList<TripEnquiry> Items, Guid? Next)> GetNoReplyDueAsync(DateTime nowUtc, Guid? after, int take)
    {
        var size = Math.Max(1, take);
        var due = nowUtc.AddHours(-TripReminderRules.NoReplyHours);
        var oldest = nowUtc.AddHours(-(TripReminderRules.NoReplyHours + TripReminderRules.WindowHours));
        var asked = TripEnquiryStatuses.Asked;
        var completed = TripStatuses.Completed;
        var taxi = TripEnquiryKinds.Taxi;
        var ids = await _context.Database
            .SqlQuery<Guid>($"""
                SELECT e."Id" AS "Value" FROM "TripEnquiries" e
                JOIN "Trips" t ON t."Id" = e."TripId"
                WHERE e."Status" = {asked} AND e."CreatedAt" <= {due} AND e."CreatedAt" > {oldest} AND e."ExpiresAt" > {nowUtc}
                  AND t."MutedAt" IS NULL AND t."Status" <> {completed}
                  AND ({after}::uuid IS NULL OR e."Id" > {after}::uuid)
                  AND NOT EXISTS (SELECT 1 FROM "TripPushLogs" p WHERE p."DedupeKey" =
                      CASE WHEN e."Kind" = {taxi} AND e."Day" IS NOT NULL AND e."CheckIn" IS NOT NULL
                                AND (e."ContextJson" ->> 'journeyId') IS NOT NULL
                           THEN 'noreply:seg:' || replace(e."TripId"::text, '-', '') || ':' || to_char(e."CheckIn", 'YYYYMMDD')
                           ELSE 'noreply:' || e."Id"::text END)
                ORDER BY e."Id"
                LIMIT {size}
                """)
            .ToListAsync();
        return await LoadPageAsync(ids, size);
    }

    private async Task<(IReadOnlyList<TripEnquiry> Items, Guid? Next)> LoadPageAsync(List<Guid> ids, int size)
    {
        if (ids.Count == 0) return (Array.Empty<TripEnquiry>(), null);
        var rows = await _dbSet.AsNoTracking().Where(e => ids.Contains(e.Id)).ToListAsync();
        var order = ids.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        return (rows.OrderBy(e => order[e.Id]).ToList(), ids.Count == size ? ids[^1] : (Guid?)null);
    }

    public async Task<(IReadOnlyList<TripEnquiry> Items, Guid? Next)> GetHostNotConfirmedDueAsync(DateTime nowUtc, Guid? after, int take)
    {
        var size = Math.Max(1, take);
        var due = nowUtc.AddHours(-TripReminderRules.HostNotConfirmedHours);
        var oldest = nowUtc.AddHours(-(TripReminderRules.HostNotConfirmedHours + TripReminderRules.WindowHours));
        var confirmed = TripEnquiryStatuses.ConfirmedByTraveller;
        var completed = TripStatuses.Completed;
        var ids = await _context.Database
            .SqlQuery<Guid>($"""
                SELECT e."Id" AS "Value" FROM "TripEnquiries" e
                JOIN "Trips" t ON t."Id" = e."TripId"
                WHERE e."Status" = {confirmed} AND e."HostConfirmAnsweredAt" IS NULL
                  AND e."HostConfirmTokenIssuedAt" <= {due} AND e."HostConfirmTokenIssuedAt" > {oldest}
                  AND t."MutedAt" IS NULL AND t."Status" <> {completed}
                  AND ({after}::uuid IS NULL OR e."Id" > {after}::uuid)
                  AND NOT EXISTS (SELECT 1 FROM "TripPushLogs" p WHERE p."DedupeKey" = 'hostnc:' || e."Id"::text)
                ORDER BY e."Id"
                LIMIT {size}
                """)
            .ToListAsync();
        return await LoadPageAsync(ids, size);
    }

    public async Task<IReadOnlyList<TripEnquiry>> GetOpenByTripAsync(Guid tripId, string kind)
        => await _dbSet
            .Where(e => e.TripId == tripId && e.Kind == kind && TripEnquiryStatuses.Open.Contains(e.Status))
            .ToListAsync();

    public async Task<TripEnquiry?> FindOpenAsync(Guid tripId, string kind, Guid listingId, short? day)
        => await _dbSet.FirstOrDefaultAsync(e =>
            e.TripId == tripId && e.Kind == kind && e.ListingId == listingId && e.Day == day
            && TripEnquiryStatuses.Open.Contains(e.Status));

    public async Task<IReadOnlyList<TripEnquiry>> GetOpenByTravellerListingAsync(Guid travellerId, string kind, Guid listingId)
        => await _dbSet.AsNoTracking()
            .Where(e => e.TravellerUserId == travellerId && e.Kind == kind && e.ListingId == listingId
                && TripEnquiryStatuses.Open.Contains(e.Status))
            .ToListAsync();

    public async Task<TripEnquiry?> GetLatestByConversationAsync(Guid conversationId)
        => await _dbSet.AsNoTracking()
            .Where(e => e.ConversationId == conversationId)
            .OrderBy(e => TripEnquiryStatuses.Open.Contains(e.Status) ? 0 : 1)
            .ThenByDescending(e => e.CreatedAt)
            .FirstOrDefaultAsync();

    public async Task<Dictionary<Guid, TripEnquiry>> GetLatestByConversationsAsync(IReadOnlyCollection<Guid> conversationIds)
    {
        if (conversationIds.Count == 0) return new Dictionary<Guid, TripEnquiry>();
        var ids = conversationIds.Distinct().ToList();
        var rows = await _dbSet.AsNoTracking()
            .Where(e => e.ConversationId != null && ids.Contains(e.ConversationId.Value))
            .ToListAsync();
        return rows
            .GroupBy(e => e.ConversationId!.Value)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(e => TripEnquiryStatuses.IsOpen(e.Status) ? 0 : 1).ThenByDescending(e => e.CreatedAt).First());
    }

    public async Task<(IReadOnlyList<TripEnquiry> Items, bool HasMore)> GetOwnerPagedAsync(Guid ownerId, string bucket, int page, int pageSize)
    {
        var size = Math.Max(1, pageSize);
        var skip = (Math.Max(1, page) - 1) * size;
        var query = _dbSet.AsNoTracking().Where(e => e.OwnerUserId == ownerId && e.Kind == TripEnquiryKinds.Stay);
        query = bucket switch
        {
            TripOwnerBuckets.New => query.Where(e => TripOwnerBuckets.NewStatuses.Contains(e.Status)),
            TripOwnerBuckets.Offered => query.Where(e => TripOwnerBuckets.OfferedStatuses.Contains(e.Status)),
            TripOwnerBuckets.Confirmed => query.Where(e => TripOwnerBuckets.ConfirmedStatuses.Contains(e.Status)),
            _ => query.Where(e => !TripOwnerBuckets.NewStatuses.Contains(e.Status)
                && !TripOwnerBuckets.OfferedStatuses.Contains(e.Status)
                && !TripOwnerBuckets.ConfirmedStatuses.Contains(e.Status)),
        };

        var rows = await query
            .OrderByDescending(e => e.CreatedAt)
            .ThenByDescending(e => e.Id)
            .Skip(skip)
            .Take(size + 1)
            .ToListAsync();
        var hasMore = rows.Count > size;
        if (hasMore) rows.RemoveAt(size);
        return (rows, hasMore);
    }

    public async Task<TripOwnerCounts> GetOwnerCountsAsync(Guid ownerId)
    {
        var groups = await _dbSet.AsNoTracking()
            .Where(e => e.OwnerUserId == ownerId && e.Kind == TripEnquiryKinds.Stay)
            .GroupBy(e => e.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync();
        int Sum(string bucket) => groups.Where(g => TripOwnerBuckets.Of(g.Status) == bucket).Sum(g => g.Count);
        return new TripOwnerCounts(
            Sum(TripOwnerBuckets.New), Sum(TripOwnerBuckets.Offered), Sum(TripOwnerBuckets.Confirmed), Sum(TripOwnerBuckets.Closed));
    }

    public async Task<int> CountNotMeAsync(Guid travellerId, DateTime sinceUtc)
        => await _dbSet.AsNoTracking().CountAsync(e =>
            e.TravellerUserId == travellerId
            && e.HostConfirmAnswer == TripHostConfirmAnswers.NotMe
            && e.HostConfirmAnsweredAt >= sinceUtc);

    public async Task<int> TryTransitionAsync(Guid id, string[] fromStatuses, string toStatus, string? failReason, DateTime nowUtc)
        => await _dbSet
            .Where(e => e.Id == id && fromStatuses.Contains(e.Status))
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, toStatus)
                .SetProperty(e => e.FailReason, failReason)
                .SetProperty(e => e.UpdatedAt, nowUtc));

    // Two index range scans (ix_tripenquiries_expires, ix_tripenquiries_checkin_unconfirmed) joined by UNION instead of
    // one OR that no single index serves; the set shrinks as rows expire.
    public async Task<IReadOnlyList<TripEnquiry>> GetExpiredUnconfirmedAsync(DateTime nowUtc, DateOnly todayIst, int take)
    {
        var byExpiry = _dbSet.AsNoTracking()
            .Where(e => TripEnquiryStatuses.Unconfirmed.Contains(e.Status) && e.ExpiresAt <= nowUtc);
        var byCheckIn = _dbSet.AsNoTracking()
            .Where(e => TripEnquiryStatuses.Unconfirmed.Contains(e.Status) && e.CheckIn != null && e.CheckIn < todayIst);
        return await byExpiry.Union(byCheckIn)
            .OrderBy(e => e.ExpiresAt)
            .Take(take)
            .ToListAsync();
    }

    public async Task<Dictionary<Guid, TripPartyInfo>> LoadPartiesAsync(IReadOnlyCollection<Guid> userIds)
    {
        if (userIds.Count == 0) return new Dictionary<Guid, TripPartyInfo>();
        var ids = userIds.Distinct().ToList();
        var users = await _context.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.Name, u.PhoneNumber, u.IsContactVisible })
            .ToListAsync();
        return users.ToDictionary(
            u => u.Id,
            u => new TripPartyInfo(u.Id, u.Name, u.PhoneNumber, u.IsContactVisible, null));
    }

    public async Task<TripPushContext> LoadPushContextAsync(TripEnquiry e, Guid recipientUserId, int? total = null, bool? confirmed = null)
    {
        var travellerName = ReadTravellerName(e);
        if (string.IsNullOrEmpty(travellerName) && e.TravellerUserId is { } travellerId)
            travellerName = TripTextSafety.Clean(await _context.Users.AsNoTracking()
                .Where(u => u.Id == travellerId).Select(u => u.Name).FirstOrDefaultAsync());
        travellerName ??= string.Empty;

        string? chatArgs = null;
        if (e.ConversationId is { } conversationId)
        {
            var c = await _context.Conversations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == conversationId);
            if (c != null && (c.OwnerId == recipientUserId || c.RenterId == recipientUserId))
            {
                var isOwner = c.OwnerId == recipientUserId;
                var otherPartyId = isOwner ? c.RenterId : c.OwnerId;
                var otherPartyName = await _context.Users.AsNoTracking()
                    .Where(u => u.Id == otherPartyId).Select(u => u.Name).FirstOrDefaultAsync() ?? "User";
                var stay = await _context.StayListings.AsNoTracking()
                    .Where(s => s.Id == c.ListingId)
                    .Select(s => new
                    {
                        s.Name, s.StayTypeId,
                        CityName = s.City != null ? s.City.Name : null,
                        DistrictName = s.District != null ? s.District.Name : null,
                    })
                    .FirstOrDefaultAsync();

                chatArgs = JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["conversationId"] = c.Id,
                    ["listingType"] = c.ListingType,
                    ["listingId"] = c.ListingId,
                    ["stayTypeId"] = stay?.StayTypeId,
                    ["otherPartyId"] = otherPartyId,
                    ["otherPartyName"] = otherPartyName,
                    ["listingTitle"] = stay?.Name ?? e.ListingName,
                    ["area"] = stay?.CityName ?? stay?.DistrictName,
                    ["isOwner"] = isOwner,
                    ["enquiryId"] = e.Id,
                });
            }
        }

        string? ownerName = null;
        if (e.OwnerUserId is { } ownerId)
            ownerName = TripTextSafety.CleanOrNull(await _context.Users.AsNoTracking()
                .Where(u => u.Id == ownerId).Select(u => u.Name).FirstOrDefaultAsync(), TripExecutionRules.HostNameMax);

        Guid? journeyId = null;
        if (e.TripId == null && e.Kind == TripEnquiryKinds.Taxi && e.TravellerUserId == recipientUserId)
            journeyId = await FindJourneyIdAsync(e, recipientUserId);

        return new TripPushContext(recipientUserId, travellerName, e.ListingName, chatArgs, total, confirmed, ownerName, journeyId);
    }

    // The journey stored in the lead's context at ask time wins; older leads fall back to the leg's LastEnquiryId,
    // then to the newest journey of that date with the driver.
    private async Task<Guid?> FindJourneyIdAsync(TripEnquiry e, Guid travellerId)
    {
        if (ReadJourneyId(e.ContextJson) is { } stored
            && await _context.Set<SavedJourney>().AsNoTracking().AnyAsync(j => j.Id == stored && j.UserId == travellerId && !j.IsDeleted))
            return stored;

        var byLeg = await _context.Set<SavedJourneyLeg>().AsNoTracking()
            .Where(l => l.LastEnquiryId == e.Id && l.Journey.UserId == travellerId && !l.Journey.IsDeleted)
            .Select(l => (Guid?)l.JourneyId)
            .FirstOrDefaultAsync();
        if (byLeg != null || e.CheckIn is not { } date) return byLeg;

        var taxiId = e.ListingId;
        return await _context.Set<SavedJourney>().AsNoTracking()
            .Where(j => j.UserId == travellerId && !j.IsDeleted && j.TravelDate == date
                && j.Legs.Any(l => l.DriverTaxiId == taxiId || l.Backup1TaxiId == taxiId || l.Backup2TaxiId == taxiId))
            .OrderByDescending(j => j.UpdatedAt)
            .Select(j => (Guid?)j.Id)
            .FirstOrDefaultAsync();
    }

    private static Guid? ReadJourneyId(string? contextJson)
    {
        if (string.IsNullOrWhiteSpace(contextJson)) return null;
        try { return JsonSerializer.Deserialize<TripEnquiryContext>(contextJson, TripJson.Options)?.JourneyId; }
        catch (JsonException) { return null; }
    }

    public async Task<(IReadOnlyList<TripEnquiry> Items, bool HasMore)> GetDriverPagedAsync(Guid driverId, string bucket, int page, int pageSize)
    {
        var size = Math.Max(1, pageSize);
        var skip = (Math.Max(1, page) - 1) * size;
        var today = TripExecutionRules.TodayIst(DateTime.UtcNow);
        var newStatuses = TripDriverBuckets.NewStatuses;
        var repliedStatuses = TripDriverBuckets.RepliedStatuses;
        var bookedStatuses = TripDriverBuckets.BookedStatuses;
        var endingStatuses = TripDriverBuckets.EndingStatuses;
        var query = _dbSet.AsNoTracking().Where(e => e.OwnerUserId == driverId && e.Kind == TripEnquiryKinds.Taxi);
        // Ended = last day before today (CheckOut = last day + 1), see TripDriverBuckets.HasEnded.
        var live = query.Where(e => !endingStatuses.Contains(e.Status) || e.CheckOut == null || e.CheckOut > today);
        query = bucket switch
        {
            TripDriverBuckets.New => live.Where(e => newStatuses.Contains(e.Status)),
            TripDriverBuckets.Replied => live.Where(e => repliedStatuses.Contains(e.Status)),
            TripDriverBuckets.Booked => live.Where(e => bookedStatuses.Contains(e.Status)),
            _ => query.Where(e =>
                (!newStatuses.Contains(e.Status) && !repliedStatuses.Contains(e.Status) && !bookedStatuses.Contains(e.Status))
                || (endingStatuses.Contains(e.Status) && e.CheckOut != null && e.CheckOut <= today)),
        };

        var rows = await query
            .OrderByDescending(e => e.CreatedAt)
            .ThenByDescending(e => e.Id)
            .Skip(skip)
            .Take(size + 1)
            .ToListAsync();
        var hasMore = rows.Count > size;
        if (hasMore) rows.RemoveAt(size);
        return (rows, hasMore);
    }

    public async Task<TripDriverCounts> GetDriverCountsAsync(Guid driverId)
    {
        var today = TripExecutionRules.TodayIst(DateTime.UtcNow);
        var groups = await _dbSet.AsNoTracking()
            .Where(e => e.OwnerUserId == driverId && e.Kind == TripEnquiryKinds.Taxi)
            .GroupBy(e => new { e.Status, Ended = e.CheckOut != null && e.CheckOut <= today })
            .Select(g => new { g.Key.Status, g.Key.Ended, Count = g.Count() })
            .ToListAsync();
        int Sum(string bucket) => groups.Where(g => TripDriverBuckets.Of(g.Status, g.Ended) == bucket).Sum(g => g.Count);
        return new TripDriverCounts(
            Sum(TripDriverBuckets.New), Sum(TripDriverBuckets.Replied), Sum(TripDriverBuckets.Booked), Sum(TripDriverBuckets.Closed));
    }

    public async Task<int> TryExpireHoldAsync(Guid id, DateTime nowUtc)
        => await _dbSet
            .Where(e => e.Id == id
                && e.Status == TripEnquiryStatuses.ConfirmedByBoth
                && (e.AdvanceStatus == TripAdvanceStatuses.Pending || e.AdvanceStatus == TripAdvanceStatuses.ProofSent)
                && e.HoldUntil != null && e.HoldUntil <= nowUtc)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, TripEnquiryStatuses.Failed)
                .SetProperty(e => e.FailReason, TripFailReasons.HoldExpired)
                .SetProperty(e => e.AdvanceStatus, TripAdvanceStatuses.HoldExpired)
                .SetProperty(e => e.UpdatedAt, nowUtc));

    public async Task<int> TryBumpReminderAsync(Guid id, int expectedCount, int nextCount, DateTime nowUtc)
    {
        var expected = (short)expectedCount;
        var next = (short)nextCount;
        return await _dbSet
            .Where(e => e.Id == id && e.HostReminderCount == expected)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.LastHostReminderAt, nowUtc)
                .SetProperty(e => e.HostReminderCount, next));
    }

    public async Task<IReadOnlyList<TripEnquiry>> GetDueHoldsAsync(DateTime nowUtc, int take)
        => await _dbSet.AsNoTracking()
            .Where(e => e.Status == TripEnquiryStatuses.ConfirmedByBoth
                && (e.AdvanceStatus == TripAdvanceStatuses.Pending || e.AdvanceStatus == TripAdvanceStatuses.ProofSent)
                && e.HoldUntil != null && e.HoldUntil <= nowUtc)
            .OrderBy(e => e.HoldUntil)
            .Take(take)
            .ToListAsync();

    // Necessary condition of HostReminderSchedule: slot n (1-based) is never before ProofSentAt + n x interval, and
    // n = HostReminderCount; the exact slot (quiet hours, merge, cap) is decided by the caller.
    public async Task<(IReadOnlyList<TripEnquiry> Items, Guid? Next)> GetDueReminderAsync(DateTime nowUtc, Guid? after, int take)
    {
        var size = Math.Max(1, take);
        var proofSent = TripAdvanceStatuses.ProofSent;
        var both = TripEnquiryStatuses.ConfirmedByBoth;
        var minutes = HostReminderSchedule.IntervalMinutes;
        var maxCount = HostReminderSchedule.MaxReminders;
        var ids = await _context.Database
            .SqlQuery<Guid>($"""
                SELECT e."Id" AS "Value" FROM "TripEnquiries" e
                WHERE e."AdvanceStatus" = {proofSent} AND e."Status" = {both}
                  AND e."HoldUntil" > {nowUtc} AND e."ProofSentAt" IS NOT NULL
                  AND e."HostReminderCount" <= {maxCount}
                  AND e."ProofSentAt" + GREATEST(e."HostReminderCount", 1) * make_interval(mins => {minutes}) <= {nowUtc}
                  AND ({after}::uuid IS NULL OR e."Id" > {after}::uuid)
                ORDER BY e."Id"
                LIMIT {size}
                """)
            .ToListAsync();
        return await LoadPageAsync(ids, size);
    }

    public async Task<IReadOnlyList<TripEnquiry>> GetExpiredFareSentAsync(DateTime nowUtc, int take)
        => await _dbSet.AsNoTracking()
            .Where(e => e.Kind == TripEnquiryKinds.Taxi && e.Status == TripEnquiryStatuses.FareSent && e.ExpiresAt <= nowUtc)
            .OrderBy(e => e.ExpiresAt)
            .Take(take)
            .ToListAsync();

    public async Task<Dictionary<Guid, (string? Phone, string? WhatsApp)>> LoadTaxiContactsAsync(IReadOnlyCollection<Guid> taxiIds)
    {
        if (taxiIds.Count == 0) return new Dictionary<Guid, (string? Phone, string? WhatsApp)>();
        var ids = taxiIds.Distinct().ToList();
        var rows = await _context.TaxiListings.AsNoTracking()
            .Where(t => ids.Contains(t.Id) && !t.IsDeleted)
            .Select(t => new { t.Id, t.ContactPhone, t.WhatsAppNumber })
            .ToListAsync();
        return rows.ToDictionary(r => r.Id, r => (Phone: r.ContactPhone, WhatsApp: r.WhatsAppNumber));
    }

    public async Task<Dictionary<Guid, int>> CountNotMeByTravellersAsync(IReadOnlyCollection<Guid> travellerIds, DateTime sinceUtc)
    {
        if (travellerIds.Count == 0) return new Dictionary<Guid, int>();
        var ids = travellerIds.Distinct().ToList();
        var rows = await _dbSet.AsNoTracking()
            .Where(e => e.TravellerUserId != null && ids.Contains(e.TravellerUserId.Value)
                && e.HostConfirmAnswer == TripHostConfirmAnswers.NotMe
                && e.HostConfirmAnsweredAt >= sinceUtc)
            .GroupBy(e => e.TravellerUserId!.Value)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToListAsync();
        return rows.ToDictionary(r => r.Id, r => r.Count);
    }

    private static string? ReadTravellerName(TripEnquiry e)
    {
        try
        {
            var ctx = JsonSerializer.Deserialize<TripEnquiryContext>(e.ContextJson, TripJson.Options);
            return string.IsNullOrWhiteSpace(ctx?.TravellerName) ? null : ctx.TravellerName;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
