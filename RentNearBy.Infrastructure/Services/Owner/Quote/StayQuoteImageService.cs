using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.DTOs.Owner.Quote;
using RentNearBy.Core.Models.Owner.Quote;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services.Owner.Quote;

// Logo and signature of the quotation master (ARCHITECTURE 3.4, 6). Shape of UserPhotoHandlers: process + upload outside any lock,
// swap the path with one FOR UPDATE statement (AssetsVersion + 1), delete the old file after the statement, delete the new file
// when the statement fails. The bytes go back to the phone only through ReadAsync (signed private download, never a URL).
public sealed class StayQuoteImageService(ApplicationDbContext db, IQuoteImageStore store, TimeProvider clock, ILogger<StayQuoteImageService> logger)
{
    public const string OrphanLog = "quote.image.orphan {Path}";

    internal sealed class SwapRow
    {
        public string? OldPath { get; set; }
        public int AssetsVersion { get; set; }
    }

    // The column is chosen from the enum, never from request text.
    internal static string Column(QuoteImageKind kind) => kind == QuoteImageKind.Logo ? "LogoPath" : "SignaturePath";

    internal const string EnsureProfileSql = """
        INSERT INTO "StayQuoteProfiles" ("UserId", "HasBank", "AssetsVersion", "CreatedAt", "UpdatedAt")
        VALUES (@user, false, 0, @now, @now) ON CONFLICT ("UserId") DO NOTHING
        """;

    internal static string SetSql(QuoteImageKind kind) => $"""
        WITH old AS (SELECT "UserId", "{Column(kind)}" AS "Path" FROM "StayQuoteProfiles" WHERE "UserId" = @user FOR UPDATE)
        UPDATE "StayQuoteProfiles" p SET "{Column(kind)}" = @path, "AssetsVersion" = p."AssetsVersion" + 1, "UpdatedAt" = @now
        FROM old WHERE p."UserId" = old."UserId"
        RETURNING old."Path" AS "OldPath", p."AssetsVersion" AS "AssetsVersion"
        """;

    // Only a stored image is cleared (and counted as a change); removing nothing is a no-op.
    internal static string ClearSql(QuoteImageKind kind) => $"""
        WITH old AS (SELECT "UserId", "{Column(kind)}" AS "Path" FROM "StayQuoteProfiles"
                     WHERE "UserId" = @user AND "{Column(kind)}" IS NOT NULL FOR UPDATE)
        UPDATE "StayQuoteProfiles" p SET "{Column(kind)}" = NULL, "AssetsVersion" = p."AssetsVersion" + 1, "UpdatedAt" = @now
        FROM old WHERE p."UserId" = old."UserId"
        RETURNING old."Path" AS "OldPath", p."AssetsVersion" AS "AssetsVersion"
        """;

    // InvalidPhotoException and PhotoBusyException of the pipeline are left to the caller (400 PhotoInvalid, 503 PhotoBusy).
    public async Task<QuoteResult<QuoteAssetsDto>> PutAsync(Guid userId, QuoteImageKind kind, Stream photo, CancellationToken ct)
    {
        var path = await store.SaveQuoteImageAsync(photo, userId, kind);
        var now = clock.GetUtcNow().UtcDateTime;
        List<SwapRow> rows;
        try
        {
            await db.Database.ExecuteSqlRawAsync(EnsureProfileSql, [User(userId), Now(now)], ct);
            rows = await db.Database.SqlQueryRaw<SwapRow>(SetSql(kind), User(userId), Now(now),
                    new NpgsqlParameter("path", NpgsqlDbType.Varchar) { Value = path })
                .ToListAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            await TryDeleteAsync(path);
            return QuoteResult<QuoteAssetsDto>.Fail(StayQuoteProfileService.UnauthorizedType);
        }
        catch
        {
            await TryDeleteAsync(path);
            throw;
        }
        if (rows.Count == 0)
        {
            await TryDeleteAsync(path);
            return QuoteResult<QuoteAssetsDto>.Fail(StayQuoteProfileService.UnauthorizedType);
        }
        var old = rows[0].OldPath;
        if (old != path && QuoteImagePaths.Belongs(old, userId, kind)) await TryDeleteAsync(old);
        return QuoteResult<QuoteAssetsDto>.Success(new QuoteAssetsDto(rows[0].AssetsVersion));
    }

    public async Task<QuoteResult<QuoteAssetsDto>> DeleteAsync(Guid userId, QuoteImageKind kind, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<SwapRow>(ClearSql(kind), User(userId), Now(clock.GetUtcNow().UtcDateTime)).ToListAsync(ct);
        if (rows.Count == 0)
        {
            var version = await db.StayQuoteProfiles.AsNoTracking().Where(p => p.UserId == userId).Select(p => (int?)p.AssetsVersion).FirstOrDefaultAsync(ct);
            return QuoteResult<QuoteAssetsDto>.Success(new QuoteAssetsDto(version ?? 0));
        }
        if (QuoteImagePaths.Belongs(rows[0].OldPath, userId, kind)) await TryDeleteAsync(rows[0].OldPath);
        return QuoteResult<QuoteAssetsDto>.Success(new QuoteAssetsDto(rows[0].AssetsVersion));
    }

    // The caller's own image only: the row is selected by the token user and the stored path must sit in his folder.
    public async Task<QuoteResult<byte[]>> ReadAsync(Guid userId, QuoteImageKind kind, CancellationToken ct)
    {
        var paths = await db.StayQuoteProfiles.AsNoTracking()
            .Where(p => p.UserId == userId)
            .Select(p => new { p.LogoPath, p.SignaturePath })
            .FirstOrDefaultAsync(ct);
        var path = kind == QuoteImageKind.Logo ? paths?.LogoPath : paths?.SignaturePath;
        if (!QuoteImagePaths.Belongs(path, userId, kind)) return QuoteResult<byte[]>.Fail(QuoteErrors.ImageMissing);
        var bytes = await store.ReadQuoteImageAsync(path!, ct);
        return bytes is { Length: > 0 } ? QuoteResult<byte[]>.Success(bytes) : QuoteResult<byte[]>.Fail(QuoteErrors.ImageMissing);
    }

    private async Task TryDeleteAsync(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { await store.DeleteQuoteImageAsync(path); }
        catch (Exception ex) { logger.LogWarning(ex, OrphanLog, path); }
    }

    private static NpgsqlParameter User(Guid userId) => new("user", NpgsqlDbType.Uuid) { Value = userId };

    private static NpgsqlParameter Now(DateTime now) => new("now", NpgsqlDbType.TimestampTz) { Value = now };
}
