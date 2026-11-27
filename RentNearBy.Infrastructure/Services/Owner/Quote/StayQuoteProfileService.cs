using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.DTOs.Owner.Quote;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Interfaces.Owner;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Core.Models.Owner.Quote;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.Owner.Be3;

namespace RentNearBy.Infrastructure.Services.Owner.Quote;

// Stay quotation master (ARCHITECTURE 3.1-3.3, 3.5, 3.7): hub, bank block, profile save, terms acceptance, name-change request.
// Every statement is keyed by the caller's UserId; no owner id, stay id or business name ever comes from a request.
// Bank values are never logged, never echoed by a validator, and leave the server only through GET /profile/bank.
public sealed class StayQuoteProfileService(
    ApplicationDbContext db, IIdentityCipher cipher, IPlanEntitlementService entitlements, IOwnerNotifier notifier,
    TimeProvider clock, ILogger<StayQuoteProfileService> logger)
{
    private static readonly JsonSerializerOptions BankJson = new(JsonSerializerDefaults.Web);
    public const string UnauthorizedType = "Unauthorized";

    public static string BankPurpose(Guid userId) => "stay-quote-bank:" + userId;

    internal sealed class HubRow
    {
        public string? StayName { get; set; }
        public string StayStatus { get; set; } = string.Empty;
        public string? StayGstin { get; set; }
        public string? StayAddress { get; set; }
        public string? WhatsAppNumber { get; set; }
        public string? UpiId { get; set; }
        public string? PendingEditJson { get; set; }
        public string? PendingEditResult { get; set; }
        public DateTime? PendingEditResultAt { get; set; }
        public DateTime StayUpdatedAt { get; set; }
        public string? UserName { get; set; }
        public string? UserPhone { get; set; }
        public bool HasProfile { get; set; }
        public string? Slogan { get; set; }
        public string? Address { get; set; }
        public string? Phone { get; set; }
        public string? Email { get; set; }
        public string? Gstin { get; set; }
        public string? BankEnc { get; set; }
        public bool? HasBank { get; set; }
        public string? TermsText { get; set; }
        public string? CancellationText { get; set; }
        public bool HasLogo { get; set; }
        public bool HasSignature { get; set; }
        public int AssetsVersion { get; set; }
        public DateTime? ProfileUpdatedAt { get; set; }
        public int? CounterMonth { get; set; }
        public short? CounterUsed { get; set; }
        public int? NextQuoteNo { get; set; }
        public int? NextProformaNo { get; set; }
        public DateTime? CounterUpdatedAt { get; set; }
        public short? AcceptedVersion { get; set; }
        public DateTime? AcceptedAt { get; set; }
        public Guid? PlanRowId { get; set; }
        public Guid? PlanId { get; set; }
        public string? PlanSource { get; set; }
        public DateTime? PlanStartsAt { get; set; }
        public DateTime? PlanEndsAt { get; set; }
    }

    // One statement: stay + user + profile + counter + latest acceptance + the entitled Stay plan row (limits cost no second query).
    internal static readonly string HubSql = $"""
        SELECT s."Name" AS "StayName", s."Status" AS "StayStatus", s."Gstin" AS "StayGstin", s."Address" AS "StayAddress",
               s."WhatsAppNumber" AS "WhatsAppNumber", s."UpiId" AS "UpiId", s."PendingEditJson" AS "PendingEditJson",
               s."PendingEditResult" AS "PendingEditResult", s."PendingEditResultAt" AS "PendingEditResultAt", s."UpdatedAt" AS "StayUpdatedAt",
               u."Name" AS "UserName", u."PhoneNumber" AS "UserPhone",
               (p."UserId" IS NOT NULL) AS "HasProfile", p."Slogan" AS "Slogan", p."Address" AS "Address", p."Phone" AS "Phone",
               p."Email" AS "Email", p."Gstin" AS "Gstin", p."BankEnc" AS "BankEnc", p."HasBank" AS "HasBank",
               p."TermsText" AS "TermsText", p."CancellationText" AS "CancellationText",
               (p."LogoPath" IS NOT NULL) AS "HasLogo", (p."SignaturePath" IS NOT NULL) AS "HasSignature",
               COALESCE(p."AssetsVersion", 0) AS "AssetsVersion", p."UpdatedAt" AS "ProfileUpdatedAt",
               c."MonthKey" AS "CounterMonth", c."Used" AS "CounterUsed", c."NextQuoteNo" AS "NextQuoteNo",
               c."NextProformaNo" AS "NextProformaNo", c."UpdatedAt" AS "CounterUpdatedAt",
               a."Version" AS "AcceptedVersion", a."AcceptedAt" AS "AcceptedAt",
               up."Id" AS "PlanRowId", up."PlanId" AS "PlanId", up."Source" AS "PlanSource", up."StartsAt" AS "PlanStartsAt", up."EndsAt" AS "PlanEndsAt"
        FROM "StayListings" s
        JOIN "Users" u ON u."Id" = s."UserId"
        LEFT JOIN "StayQuoteProfiles" p ON p."UserId" = s."UserId"
        LEFT JOIN "StayQuoteCounters" c ON c."UserId" = s."UserId"
        LEFT JOIN LATERAL (
            SELECT t."Version", t."AcceptedAt" FROM "StayQuoteTermsAcceptances" t
            WHERE t."UserId" = s."UserId" ORDER BY t."Version" DESC LIMIT 1) a ON true
        LEFT JOIN LATERAL (
            SELECT x."Id", x."PlanId", x."Source", x."StartsAt", x."EndsAt" FROM "UserPlans" x
            WHERE x."UserId" = s."UserId" AND x."FeatureKey" = '{ListingKinds.Stay}' AND {PlanRowSql.EntitledSql("x")}
            ORDER BY x."EndsAt" DESC LIMIT 1) up ON true
        WHERE s."UserId" = @user AND s."IsDeleted" = false
        """;

    public sealed record HubAnswer(QuoteHubDto? Hub, string ETag, bool NotModified);

    public async Task<QuoteResult<HubAnswer>> GetHubAsync(Guid userId, string? ifNoneMatch, CancellationToken ct)
    {
        var row = (await db.Database.SqlQueryRaw<HubRow>(HubSql, User(userId)).ToListAsync(ct)).FirstOrDefault();
        if (row == null) return QuoteResult<HubAnswer>.Fail(QuoteErrors.StayNotFound);

        var now = clock.GetUtcNow().UtcDateTime;
        var month = SocialMonth.Key(IstTime.TodayIst(now));
        var limits = await entitlements.ComputeFromRowAsync(ListingKinds.Stay,
            PlanRow(row.PlanRowId, row.PlanId, row.PlanSource, row.PlanStartsAt, row.PlanEndsAt));
        var usage = new QuoteUsageDto(StayQuoteUsageReader.UsedThisMonth(row.CounterMonth, row.CounterUsed, month),
            limits.StayQuotationsPerMonth, SocialMonth.ResetsAtUtc(now), limits.CanUpgrade);
        var pending = StayPendingEditJson.Read(row.PendingEditJson);
        var pendingName = pending != null && pending.Fields.Contains(StayReviewFields.Name) ? pending.Asked.Name : null;

        var etag = ETagOf(row, usage, pendingName);
        if (ifNoneMatch != null && ifNoneMatch == etag) return QuoteResult<HubAnswer>.Success(new HubAnswer(null, etag, true));

        var lastResult = StayReviewFields.StripVisible(row.PendingEditResult, row.PendingEditResultAt, pending != null, now)
            ? row.PendingEditResult
            : null;
        var stay = new QuoteHubStayDto(row.StayName, row.StayStatus == StayStatuses.Approved, pendingName, lastResult, row.StayGstin != null);

        var bankUnreadable = false;
        QuoteProfileDto? profile = null;
        if (row.HasProfile)
        {
            var (summary, unreadable) = Summary(userId, row.BankEnc);
            bankUnreadable = unreadable;
            profile = new QuoteProfileDto(row.Slogan, row.Address, row.Phone, row.Email, QuoteGstinRule.Printable(row.Gstin, row.StayGstin), summary,
                row.TermsText, row.CancellationText,
                row.HasLogo, row.HasSignature, row.AssetsVersion, row.ProfileUpdatedAt ?? now);
        }

        var hub = new QuoteHubDto(
            stay,
            string.IsNullOrWhiteSpace(row.UserName) ? null : row.UserName.Trim(),
            profile,
            new QuoteDefaultsDto(row.StayAddress, row.WhatsAppNumber ?? row.UserPhone, row.UpiId),
            new QuoteTermsStateDto(QuoteTerms.CurrentVersion, row.AcceptedVersion, row.AcceptedAt),
            usage,
            new QuoteNextDto(row.NextQuoteNo ?? 1, row.NextProformaNo ?? 1),
            cipher.IsConfigured,
            bankUnreadable,
            QuoteRules.MinTemplateVersion,
            QuoteRules.PreviewWatermark,
            QuoteRules.MaxFreePerWindow);
        return QuoteResult<HubAnswer>.Success(new HubAnswer(hub, etag, false));
    }

    // A weak validator of everything the hub shows; a match answers 304 without a decrypt or a body.
    internal static string ETagOf(HubRow row, QuoteUsageDto usage, string? pendingName)
    {
        var key = string.Join('|',
            row.StayName, row.StayStatus, row.StayGstin is null ? "0" : "1", row.StayUpdatedAt.Ticks, row.UserName, pendingName,
            row.PendingEditResult, row.PendingEditResultAt?.Ticks, row.HasProfile, row.ProfileUpdatedAt?.Ticks, row.AssetsVersion,
            row.CounterUpdatedAt?.Ticks, row.AcceptedVersion, QuoteTerms.CurrentVersion, row.PlanRowId, row.PlanEndsAt?.Ticks,
            usage.Used, usage.Limit, usage.CanUpgrade, usage.ResetsAt.Ticks, QuoteRules.MinTemplateVersion, QuoteRules.MaxFreePerWindow,
            QuoteRules.PreviewWatermark);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return "W/\"" + Convert.ToHexStringLower(hash.AsSpan(0, 12)) + "\"";
    }

    public async Task<QuoteResult<QuoteBankDto?>> GetBankAsync(Guid userId, CancellationToken ct)
    {
        if (!cipher.IsConfigured) return QuoteResult<QuoteBankDto?>.Fail(QuoteErrors.BankUnavailable);
        var blob = await db.StayQuoteProfiles.AsNoTracking().Where(p => p.UserId == userId).Select(p => p.BankEnc).FirstOrDefaultAsync(ct);
        if (blob == null) return QuoteResult<QuoteBankDto?>.Success(null);
        return QuoteResult<QuoteBankDto?>.Success(Open(userId, blob));
    }

    internal sealed class ProfileRow
    {
        public string? Slogan { get; set; }
        public string? Address { get; set; }
        public string? Phone { get; set; }
        public string? Email { get; set; }
        public string? Gstin { get; set; }
        public string? BankEnc { get; set; }
        public bool HasBank { get; set; }
        public string? TermsText { get; set; }
        public string? CancellationText { get; set; }
        public bool HasLogo { get; set; }
        public bool HasSignature { get; set; }
        public int AssetsVersion { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    // One upsert. Keep leaves BankEnc / HasBank as stored; Set and Clear replace them.
    internal const string UpsertProfileSql = """
        INSERT INTO "StayQuoteProfiles" ("UserId", "Slogan", "Address", "Phone", "Email", "Gstin", "BankEnc", "HasBank",
                                         "TermsText", "CancellationText", "AssetsVersion", "CreatedAt", "UpdatedAt")
        VALUES (@user, @slogan, @address, @phone, @email, @gstin, @bank, @bank IS NOT NULL, @terms, @cancellation, 0, @now, @now)
        ON CONFLICT ("UserId") DO UPDATE SET
            "Slogan" = EXCLUDED."Slogan", "Address" = EXCLUDED."Address", "Phone" = EXCLUDED."Phone", "Email" = EXCLUDED."Email",
            "Gstin" = EXCLUDED."Gstin", "TermsText" = EXCLUDED."TermsText", "CancellationText" = EXCLUDED."CancellationText",
            "BankEnc" = CASE WHEN @keep THEN "StayQuoteProfiles"."BankEnc" ELSE EXCLUDED."BankEnc" END,
            "HasBank" = CASE WHEN @keep THEN "StayQuoteProfiles"."HasBank" ELSE EXCLUDED."HasBank" END,
            "UpdatedAt" = EXCLUDED."UpdatedAt"
        RETURNING "Slogan", "Address", "Phone", "Email", "Gstin", "BankEnc", "HasBank", "TermsText", "CancellationText",
                  ("LogoPath" IS NOT NULL) AS "HasLogo", ("SignaturePath" IS NOT NULL) AS "HasSignature", "AssetsVersion", "UpdatedAt"
        """;

    public async Task<QuoteResult<QuoteProfileDto>> SaveProfileAsync(Guid userId, QuoteProfileRequest request, CancellationToken ct)
    {
        var stay = await db.StayListings.AsNoTracking()
            .Where(s => s.UserId == userId && !s.IsDeleted)
            .Select(s => new { s.Gstin })
            .FirstOrDefaultAsync(ct);
        if (stay == null) return QuoteResult<QuoteProfileDto>.Fail(QuoteErrors.StayNotFound);

        var gstin = QuoteText.Clean(request.Gstin) is { } g ? StayRegistrationRules.NormalizeGstin(g) : null;
        if (!QuoteGstinRule.Allowed(gstin, stay.Gstin)) return QuoteResult<QuoteProfileDto>.Fail(QuoteErrors.GstinMismatch);

        var mode = request.BankMode ?? QuoteBankModes.Keep;
        string? bankBlob = null;
        if (mode == QuoteBankModes.Set)
        {
            if (!cipher.IsConfigured) return QuoteResult<QuoteProfileDto>.Fail(QuoteErrors.BankUnavailable);
            bankBlob = cipher.Protect(JsonSerializer.Serialize(Normalise(request.Bank!), BankJson), BankPurpose(userId));
        }

        var now = clock.GetUtcNow().UtcDateTime;
        List<ProfileRow> rows;
        try
        {
            rows = await db.Database.SqlQueryRaw<ProfileRow>(UpsertProfileSql,
                    User(userId),
                    Text("slogan", QuoteText.Clean(request.Slogan)),
                    Text("address", QuoteText.Clean(request.Address)),
                    Text("phone", QuoteText.Clean(request.Phone)),
                    Text("email", QuoteText.Clean(request.Email)?.ToLowerInvariant()),
                    Text("gstin", gstin),
                    Text("bank", bankBlob),
                    Text("terms", QuoteText.Clean(request.TermsText)),
                    Text("cancellation", QuoteText.Clean(request.CancellationText)),
                    new NpgsqlParameter("keep", NpgsqlDbType.Boolean) { Value = mode == QuoteBankModes.Keep },
                    new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now })
                .ToListAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            return QuoteResult<QuoteProfileDto>.Fail(UnauthorizedType);
        }
        var r = rows[0];
        var (summary, _) = Summary(userId, r.BankEnc);
        return QuoteResult<QuoteProfileDto>.Success(new QuoteProfileDto(r.Slogan, r.Address, r.Phone, r.Email, r.Gstin, summary,
            r.TermsText, r.CancellationText, r.HasLogo, r.HasSignature, r.AssetsVersion, r.UpdatedAt));
    }

    internal const string AcceptTermsSql = """
        WITH ins AS (
            INSERT INTO "StayQuoteTermsAcceptances" ("UserId", "Version", "AcceptedAt", "AppVersion")
            VALUES (@user, @version, @now, @app)
            ON CONFLICT ("UserId", "Version") DO NOTHING
            RETURNING "AcceptedAt")
        SELECT "AcceptedAt" AS "Value" FROM ins
        UNION ALL
        SELECT t."AcceptedAt" FROM "StayQuoteTermsAcceptances" t WHERE t."UserId" = @user AND t."Version" = @version
        LIMIT 1
        """;

    // Idempotent: the first accepted time is kept. Only the current version is accepted.
    public async Task<QuoteResult<QuoteTermsAcceptedDto>> AcceptTermsAsync(Guid userId, QuoteTermsAcceptRequest request, CancellationToken ct)
    {
        if (request.Version is not { } version || !QuoteTerms.IsKnown(version))
            return QuoteResult<QuoteTermsAcceptedDto>.Fail(QuoteErrors.TermsVersionUnknown);
        if (!await db.StayListings.AsNoTracking().AnyAsync(s => s.UserId == userId && !s.IsDeleted, ct))
            return QuoteResult<QuoteTermsAcceptedDto>.Fail(QuoteErrors.StayNotFound);
        List<DateTime> at;
        try
        {
            at = await db.Database.SqlQueryRaw<DateTime>(AcceptTermsSql,
                    User(userId),
                    new NpgsqlParameter("version", NpgsqlDbType.Smallint) { Value = (short)version },
                    new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = clock.GetUtcNow().UtcDateTime },
                    Text("app", QuoteText.Clean(request.AppVersion)))
                .ToListAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            return QuoteResult<QuoteTermsAcceptedDto>.Fail(UnauthorizedType);
        }
        if (at.Count == 0)
        {
            // Two taps at once: this one waited on the other's insert and did nothing, and its statement snapshot (taken before the
            // wait) cannot see the committed row. A new statement can: read the first accepted time once more.
            var accepted = (short)version;
            var first = await db.StayQuoteTermsAcceptances.AsNoTracking()
                .Where(t => t.UserId == userId && t.Version == accepted)
                .Select(t => (DateTime?)t.AcceptedAt)
                .FirstOrDefaultAsync(ct);
            if (first == null) return QuoteResult<QuoteTermsAcceptedDto>.Fail(UnauthorizedType);
            at = [first.Value];
        }
        return QuoteResult<QuoteTermsAcceptedDto>.Success(new QuoteTermsAcceptedDto(version, DateTime.SpecifyKind(at[0], DateTimeKind.Utc)));
    }

    // ARCHITECTURE 3.7: the name change rides the existing pending-edit path (one shared writer with the edit screen); the other
    // waiting fields are kept, the admin sees the note and the mixed-script flag, the approve/reject pushes already exist.
    public async Task<QuoteResult<QuoteNameChangeDto>> RequestNameChangeAsync(Guid userId, QuoteNameChangeRequest request, CancellationToken ct)
    {
        var stay = await db.StayListings
            .Include(s => s.District)
            .Include(s => s.City)
            .FirstOrDefaultAsync(s => s.UserId == userId && !s.IsDeleted, ct);
        if (stay == null) return QuoteResult<QuoteNameChangeDto>.Fail(QuoteErrors.StayNotFound);
        if (stay.Status != StayStatuses.Approved) return QuoteResult<QuoteNameChangeDto>.Fail(QuoteErrors.StayNotApproved);

        var now = clock.GetUtcNow().UtcDateTime;
        var asked = StayPendingEditWriter.Basis(stay) with { Name = request.Name!.Trim() };
        var outcome = StayPendingEditWriter.Apply(stay, asked, now, QuoteText.Clean(request.Note));
        if (outcome != StayReviewFields.SaveOutcome.Unchanged) stay.UpdatedAt = now;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.SaveChangesAsync(ct);
            if (outcome == StayReviewFields.SaveOutcome.NewPending)
                await notifier.ClearAsync(NoticeKinds.StayEditRejected, NoticeKinds.NoTrip, [userId], ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(ct);
            return QuoteResult<QuoteNameChangeDto>.Fail(OwnerErrorTypes.StaleEdit);
        }

        var pending = StayPendingEditJson.Read(stay.PendingEditJson);
        var pendingName = pending != null && pending.Fields.Contains(StayReviewFields.Name) ? pending.Asked.Name : null;
        return QuoteResult<QuoteNameChangeDto>.Success(new QuoteNameChangeDto(pendingName));
    }

    // Masked summary of the stored blob; an unreadable blob (lost key, tampered, another owner's) is reported, never thrown.
    internal (QuoteBankSummaryDto Summary, bool Unreadable) Summary(Guid userId, string? blob)
    {
        if (blob == null) return (QuoteBankSummaryDto.None, false);
        var bank = Open(userId, blob);
        if (bank == null) return (QuoteBankSummaryDto.None, true);
        return (new QuoteBankSummaryDto(true, bank.Bank, bank.Branch, QuoteText.Last4(bank.AccountNo), bank.Ifsc, QuoteText.MaskUpi(bank.Upi)), false);
    }

    internal QuoteBankDto? Open(Guid userId, string blob)
    {
        if (!cipher.IsConfigured) return null;
        try
        {
            return JsonSerializer.Deserialize<QuoteBankDto>(cipher.Unprotect(blob, BankPurpose(userId)), BankJson);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
        {
            logger.LogWarning("Quote bank block unreadable for {UserId}: {Error}", userId, ex.GetType().Name);
            return null;
        }
    }

    internal static QuoteBankDto Normalise(QuoteBankRequest b) => new(
        QuoteText.Clean(b.Holder),
        QuoteText.Clean(b.AccountNo),
        QuoteText.Clean(b.Ifsc)?.ToUpperInvariant(),
        QuoteText.Clean(b.Bank),
        QuoteText.Clean(b.Branch),
        QuoteText.Clean(b.Upi));

    internal static EntitledPlanRow? PlanRow(Guid? id, Guid? planId, string? source, DateTime? startsAt, DateTime? endsAt)
        => id is { } i && planId is { } p && source != null && startsAt is { } s && endsAt is { } e ? new EntitledPlanRow(i, p, source, s, e) : null;

    private static NpgsqlParameter User(Guid userId) => new("user", NpgsqlDbType.Uuid) { Value = userId };

    private static NpgsqlParameter Text(string name, string? value) => new(name, NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value };
}

// D-6: when the stay carries a GSTIN from onboarding, the quotation GSTIN must be the same one (or left empty).
public static class QuoteGstinRule
{
    public static bool Allowed(string? quoteGstin, string? stayGstin)
        => quoteGstin == null || stayGstin == null
           || string.Equals(quoteGstin, StayRegistrationRules.NormalizeGstin(stayGstin), StringComparison.Ordinal);

    // D-6 at read time too: a quotation GSTIN saved while the stay had none stops printing once the stay carries a different one
    // (the app prints the GSTIN the hub gives). The stored value is kept; the owner sees an empty field and can save the right one.
    public static string? Printable(string? quoteGstin, string? stayGstin) => Allowed(quoteGstin, stayGstin) ? quoteGstin : null;
}
