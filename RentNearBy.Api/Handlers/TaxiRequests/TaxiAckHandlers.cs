using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.DTOs.TaxiRequests;
using RentNearBy.Core.Entities.TaxiRequests;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.TaxiRequests;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers.TaxiRequests;

public sealed record TaxiAckOffer(Guid DriverListingId, DateTime OfferedAt, DateTime? ExpiresAt = null);

public enum TaxiAckOutcome
{
    Stored,
    Repeat,
    NotFound,
}

public sealed record TaxiAckResult(TaxiAckOutcome Outcome, bool ResetMissedRings = false);

// The database side of an ack, so the rule below runs the same against Postgres and in tests.
public interface ITaxiAckSteps
{
    Task<TaxiAckOffer?> FindOfferAsync(Guid offerId, CancellationToken ct);

    // First write wins, except that a good ack replaces a stored late one: false when nothing was written.
    Task<bool> TryRecordFirstAsync(Guid offerId, DateTime receivedAtUtc, bool late, CancellationToken ct);

    Task ResetRingMissAsync(Guid driverListingId, CancellationToken ct);
}

// Contract section 7. The token is checked first (constant time, no database read for a bad one). The first ack is stored
// with PushLate = late or not shown; only that first ack, shown and in time, resets the driver's missed rings (section 6).
public static class TaxiAckFlow
{
    // An ack that reaches the server after the offer's own expiry is late too (it can no longer reset a counted miss).
    public static bool IsLate(TaxiRingAckDto body, DateTime? nowUtc = null, DateTime? expiresAtUtc = null)
        => body.Late || !body.Shown || (nowUtc is { } now && expiresAtUtc is { } exp && now >= DateTime.SpecifyKind(exp, DateTimeKind.Utc));

    // The device clock is not trusted beyond the ring's own life: the stored time stays between the ring and now.
    public static DateTime ReceivedAt(TaxiRingAckDto body, DateTime offeredAtUtc, DateTime nowUtc)
    {
        var at = DateTime.SpecifyKind(body.ReceivedAt, DateTimeKind.Utc);
        if (at < offeredAtUtc) return offeredAtUtc;
        return at > nowUtc ? nowUtc : at;
    }

    public static async Task<TaxiAckResult> RunAsync(ITaxiAckSteps steps, TaxiAckTokens tokens, Guid offerId, TaxiRingAckDto? body,
        DateTime nowUtc, TaxiRequestMetrics? metrics, CancellationToken ct)
    {
        if (body is null || !tokens.IsValid(offerId, body.AckToken)) return new TaxiAckResult(TaxiAckOutcome.NotFound);
        var offer = await steps.FindOfferAsync(offerId, ct);
        if (offer is null) return new TaxiAckResult(TaxiAckOutcome.NotFound);

        var offeredAt = DateTime.SpecifyKind(offer.OfferedAt, DateTimeKind.Utc);
        var receivedAt = ReceivedAt(body, offeredAt, nowUtc);
        var late = IsLate(body, nowUtc, offer.ExpiresAt);
        if (!await steps.TryRecordFirstAsync(offerId, receivedAt, late, ct)) return new TaxiAckResult(TaxiAckOutcome.Repeat);

        metrics?.RingAcked((receivedAt - offeredAt).TotalSeconds);
        if (late) return new TaxiAckResult(TaxiAckOutcome.Stored);
        await steps.ResetRingMissAsync(offer.DriverListingId, ct);
        return new TaxiAckResult(TaxiAckOutcome.Stored, ResetMissedRings: true);
    }
}

public sealed class EfTaxiAckSteps(ApplicationDbContext db, TaxiRequestStore store) : ITaxiAckSteps
{
    public const string RecordFirstSql = """
        UPDATE "TaxiRequestOffers" SET "PushReceivedAt" = @at, "PushLate" = @late
        WHERE "Id" = @id AND ("PushReceivedAt" IS NULL OR ("PushLate" AND NOT @late))
        """;

    public Task<TaxiAckOffer?> FindOfferAsync(Guid offerId, CancellationToken ct)
        => db.Set<TaxiRequestOffer>().AsNoTracking().Where(o => o.Id == offerId)
            .Select(o => new TaxiAckOffer(o.DriverListingId, o.OfferedAt, o.ExpiresAt)).FirstOrDefaultAsync(ct);

    public async Task<bool> TryRecordFirstAsync(Guid offerId, DateTime receivedAtUtc, bool late, CancellationToken ct)
        => await db.Database.ExecuteSqlRawAsync(RecordFirstSql,
        [
            new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = receivedAtUtc },
            new NpgsqlParameter("late", NpgsqlDbType.Boolean) { Value = late },
            new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = offerId },
        ], ct) > 0;

    public Task ResetRingMissAsync(Guid driverListingId, CancellationToken ct) => store.ResetRingMissAsync(db, driverListingId, ct);
}

// POST /taxi-requests/offers/{offerId}/ack: no session (push background isolate), open while the switch is off.
public static class TaxiAckHandlers
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    // The token is checked first (HMAC, no database read). A bad token is charged to the caller's IP, so drivers behind one
    // carrier NAT never share a bucket with valid acks; a valid token is charged to its own offer only.
    public static (string Key, int PerMinute) LimitFor(Guid offerId, string ip, bool tokenValid) => tokenValid
        ? ($"taxi:ack:offer:{offerId}", TaxiDispatchRules.AckPerMinutePerOffer)
        : ($"taxi:ack:ip:{ip}", TaxiDispatchRules.AckPerMinutePerIp);

    public static async Task<IResult> Ack(Guid offerId, TaxiRingAckDto? body, HttpContext http, ApplicationDbContext db, TaxiAckTokens tokens,
        TaxiRequestStore store, IRateLimitService rateLimiter, TaxiRequestMetrics metrics, CancellationToken ct)
    {
        var valid = body is not null && tokens.IsValid(offerId, body.AckToken);
        var (key, perMinute) = LimitFor(offerId, http.Connection.RemoteIpAddress?.ToString() ?? "unknown", valid);
        var limit = await rateLimiter.CheckAsync(key, perMinute, Minute);
        if (!limit.IsAllowed)
        {
            http.Response.Headers["Retry-After"] = ((int)Math.Ceiling((limit.RetryAfter ?? Minute).TotalSeconds)).ToString();
            return TooManyRequestsResponse();
        }

        // A bad token never opens a transaction; the stored ack and its counter reset commit together.
        if (!valid) return NotFound();
        TaxiAckResult result;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            result = await TaxiAckFlow.RunAsync(new EfTaxiAckSteps(db, store), tokens, offerId, body, DateTime.UtcNow, metrics, ct);
            await tx.CommitAsync(ct);
        }
        return result.Outcome == TaxiAckOutcome.NotFound ? NotFound() : NoContentResponse();
    }

    private static IResult NotFound()
        => ErrorResponse(404, TaxiRequestErrorTypes.Messages[TaxiRequestErrorTypes.OfferNotFound], TaxiRequestErrorTypes.OfferNotFound);
}
