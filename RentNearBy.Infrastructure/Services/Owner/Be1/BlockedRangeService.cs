using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.DTOs.Owner;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.TaxiRequests;
using static RentNearBy.Infrastructure.Services.Owner.Be1.OwnerCalendarSql;

namespace RentNearBy.Infrastructure.Services.Owner.Be1;

public sealed record BlockedRangeWrite(BlockedRangeRow Block, int ConfirmedInside, int OpenEnquiriesInside, IReadOnlyList<MeterDto> Meters, bool Changed);

// K1 to K4 (contract 2.2). Writes take the stay row lock (level 5) so the limit and the overlap check see every committed block.
// A block never touches a confirmed booking: with confirmed bookings inside, the first save answers 409 BlockedRangeNeedsConfirm
// and writes nothing; the save with confirm=true goes through. The caller bumps the stay cache after commit when Changed.
public sealed class BlockedRangeService(ApplicationDbContext db, ILogger<BlockedRangeService> logger)
{
    public async Task<IReadOnlyList<BlockedRangeRow>> ListAsync(OwnerContext ctx, CancellationToken ct = default)
        => await db.Database.SqlQueryRaw<BlockedRangeRow>(ActiveBlocks, Uuid("l", ctx.ListingId), Date("today", ctx.Today)).ToListAsync(ct);

    public static IReadOnlyList<MeterDto> Meters(OwnerContext ctx, long activeBlocks)
        => [new MeterDto(PlanFeatureKeys.BlockedRanges, (int)activeBlocks, ctx.Limits.BlockedRanges)];

    public async Task<Be1Result<BlockedRangeWrite>> CreateAsync(
        OwnerContext ctx, Guid id, DateOnly from, DateOnly to, string? reason, bool confirm, CancellationToken ct = default)
    {
        if (id == Guid.Empty) return new Be1Error(OwnerErrorTypes.BadRequest, "Missing id.");
        if (BlockedRangeRules.Validate(from, to, reason, ctx.Today) is { } invalid) return new Be1Error(invalid.Type, invalid.Message);

        return await PgRetry.OnceAsync<Be1Result<BlockedRangeWrite>>(db, async c =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(c);
            if (!await OwnerLocks.LockStayAsync(db, ctx.ListingId, c)) return Be1Error.Of(OwnerErrorTypes.ListingNotFound);
            var check = await CheckAsync(ctx, id, from, to, c);

            if (ParseBlock(check.Existing) is { } existing)
            {
                if (existing.StayListingId != ctx.ListingId) return Be1Error.Of(OwnerErrorTypes.IdTaken);
                await tx.CommitAsync(c);
                return Be1Result<BlockedRangeWrite>.Success(new BlockedRangeWrite(existing, (int)check.ConfirmedInside, (int)check.OpenInside, Meters(ctx, check.Active), false));
            }

            var limit = ctx.Limits.BlockedRanges;
            if (check.Active >= limit)
                return new Be1Error(OwnerErrorTypes.BlockedRangeLimit, OwnerErrorTypes.LimitMessage(OwnerErrorTypes.BlockedRangeLimit, limit, ctx.Limits.CanUpgrade),
                    new PlanLimitRefusalDto(PlanFeatureKeys.BlockedRanges, limit, (int)check.Active, ctx.Limits.CanUpgrade));
            if (Refusal(check, confirm) is { } refused) return refused;

            var inserted = await db.Database.SqlQueryRaw<BlockedRangeRow>(BlockInsert,
                Uuid("id", id), Uuid("l", ctx.ListingId), Date("from", from), Date("to", to), Text("reason", reason)).ToListAsync(c);
            if (inserted.Count == 0) return Be1Error.Of(OwnerErrorTypes.IdTaken);
            await tx.CommitAsync(c);
            return Be1Result<BlockedRangeWrite>.Success(
                new BlockedRangeWrite(inserted[0], (int)check.ConfirmedInside, (int)check.OpenInside, Meters(ctx, check.Active + 1), true));
        }, ct, logger);
    }

    // K3: never limited (an ended block cannot be edited, so no edit adds an active block); overlap and confirmation as K2.
    // A block that already started may keep its start date.
    public async Task<Be1Result<BlockedRangeWrite>> UpdateAsync(
        OwnerContext ctx, Guid id, int? version, DateOnly from, DateOnly to, string? reason, bool confirm, CancellationToken ct = default)
    {
        if (version is not { } v) return new Be1Error(OwnerErrorTypes.BadRequest, "Missing version.");

        return await PgRetry.OnceAsync<Be1Result<BlockedRangeWrite>>(db, async c =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(c);
            if (!await OwnerLocks.LockStayAsync(db, ctx.ListingId, c)) return Be1Error.Of(OwnerErrorTypes.ListingNotFound);
            var check = await CheckAsync(ctx, id, from, to, c);
            if (ParseBlock(check.Existing) is not { } stored || stored.StayListingId != ctx.ListingId)
                return new Be1Error(Be1ErrorTypes.NotFound, "Blocked range not found.");
            var same = stored.DateFrom == from && stored.DateTo == to && stored.Reason == reason;
            // D-07 / FB-04: an ended block is read-only (Delete stays allowed), so it can never be extended into an active one.
            if (!same && OwnerCalendarRules.Ended(stored.DateTo, ctx.Today))
                return new Be1Error(OwnerErrorTypes.BlockedRangeInvalid, OwnerErrorTypes.BlockEnded);
            if (BlockedRangeRules.Validate(from, to, reason, ctx.Today, keptStart: stored.DateFrom) is { } invalid) return new Be1Error(invalid.Type, invalid.Message);
            if (stored.Version != v)
            {
                await tx.CommitAsync(c);
                return same
                    ? Be1Result<BlockedRangeWrite>.Success(new BlockedRangeWrite(stored, (int)check.ConfirmedInside, (int)check.OpenInside, Meters(ctx, check.Active), false))
                    : new Be1Error(OwnerErrorTypes.StaleEdit, OwnerErrorTypes.MessageOf(OwnerErrorTypes.StaleEdit), null, null, stored);
            }
            if (Refusal(check, confirm) is { } refused) return refused;

            var updated = await db.Database.SqlQueryRaw<BlockedRangeRow>(BlockUpdate,
                Uuid("id", id), Uuid("l", ctx.ListingId), Int("v", v), Date("from", from), Date("to", to), Text("reason", reason)).ToListAsync(c);
            await tx.CommitAsync(c);
            var active = check.Active - (stored.DateTo >= ctx.Today ? 1 : 0) + (to >= ctx.Today ? 1 : 0);
            return Be1Result<BlockedRangeWrite>.Success(
                new BlockedRangeWrite(updated[0], (int)check.ConfirmedInside, (int)check.OpenInside, Meters(ctx, active), !same));
        }, ct, logger);
    }

    // K4: idempotent; true = a row was deleted (the caller bumps the stay cache).
    public async Task<bool> DeleteAsync(OwnerContext ctx, Guid id, CancellationToken ct = default)
        => await db.Database.ExecuteSqlRawAsync(BlockDelete, [Uuid("id", id), Uuid("l", ctx.ListingId)], ct) > 0;

    private async Task<BlockCheckRow> CheckAsync(OwnerContext ctx, Guid id, DateOnly from, DateOnly to, CancellationToken ct)
        => (await db.Database.SqlQueryRaw<BlockCheckRow>(BlockCheck,
            Uuid("l", ctx.ListingId), Uuid("me", ctx.UserId), Uuid("id", id), Date("today", ctx.Today), Date("from", from), Date("to", to))
            .ToListAsync(ct))[0];

    private static Be1Error? Refusal(BlockCheckRow check, bool confirm)
    {
        if (check.Overlap) return Be1Error.Of(OwnerErrorTypes.BlockedRangeOverlap);
        if (check.ConfirmedInside > 0 && !confirm)
            return new Be1Error(OwnerErrorTypes.BlockedRangeNeedsConfirm, BlockedRangeRules.NeedsConfirmMessage((int)check.ConfirmedInside),
                new StayBlockedRangeConfirmDto((int)check.ConfirmedInside, (int)check.OpenInside));
        return null;
    }

    private static BlockedRangeRow? ParseBlock(string? json)
        => string.IsNullOrWhiteSpace(json)
            ? null
            : System.Text.Json.JsonSerializer.Deserialize<BlockedRangeRow>(json, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
}
