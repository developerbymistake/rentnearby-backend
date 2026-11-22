using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Api.Handlers.Photos;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

// Profile picture (any role): replace only, no delete route. The old file goes after the statement committed.
public static class UserPhotoHandlers
{
    public const string SetProfilePhotoSql = """
        WITH old AS (SELECT "Id", "ProfilePhotoPath" FROM "Users" WHERE "Id" = @user FOR UPDATE)
        UPDATE "Users" u SET "ProfilePhotoUrl" = @url, "ProfilePhotoPath" = @path, "ProfilePhotoUpdatedAt" = now(), "UpdatedAt" = now()
        FROM old WHERE u."Id" = old."Id"
        RETURNING old."ProfilePhotoPath" AS "OldPath"
        """;

    private sealed class ProfilePhotoSwapRow
    {
        public string? OldPath { get; set; }
    }

    public static async Task<IResult> UploadPhoto(
        IFormFile? photo, ClaimsPrincipal principal, ApplicationDbContext db, IUnitOfWork unitOfWork,
        IPhotoService photoService, IMemoryCache cache, IUsernameService usernames, IServiceProvider sp)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        if (PhotoUpload.Check(photo) is { } bad) return bad;

        var logger = PhotoCleanup.Logger(sp);
        string url, filePath;
        using (var stream = photo!.OpenReadStream())
            (url, filePath) = await photoService.SaveProfilePhotoAsync(stream, photo.FileName, userId);

        List<ProfilePhotoSwapRow> rows;
        try
        {
            rows = await db.Database.SqlQueryRaw<ProfilePhotoSwapRow>(SetProfilePhotoSql,
                    new NpgsqlParameter("user", NpgsqlDbType.Uuid) { Value = userId },
                    new NpgsqlParameter("url", NpgsqlDbType.Varchar) { Value = url },
                    new NpgsqlParameter("path", NpgsqlDbType.Varchar) { Value = filePath })
                .ToListAsync();
        }
        catch
        {
            await PhotoCleanup.TryDeleteAsync(photoService, filePath, logger);
            throw;
        }
        if (rows.Count == 0)
        {
            await PhotoCleanup.TryDeleteAsync(photoService, filePath, logger);
            return NotFoundResponse("User not found");
        }

        var oldPath = rows[0].OldPath;
        if (oldPath != filePath) await PhotoCleanup.TryDeleteAsync(photoService, oldPath, logger);

        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null) return NotFoundResponse("User not found");
        return OkResponse(await UsersHandlers.BuildUserDtoAsync(user, unitOfWork, cache, usernames));
    }
}
