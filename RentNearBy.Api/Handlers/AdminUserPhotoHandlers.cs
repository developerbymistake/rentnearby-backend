using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Api.Hubs;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

// Admin removes a user's profile picture: columns cleared (UpdatedAt set = "removed by the admin"), notice in the same
// transaction, the file after the commit. The app asks the user for a new picture.
public static class AdminUserPhotoHandlers
{
    public const string ClearProfilePhotoSql = """
        WITH old AS (SELECT "Id", "ProfilePhotoPath" FROM "Users" WHERE "Id" = @id AND "ProfilePhotoPath" IS NOT NULL FOR UPDATE)
        UPDATE "Users" u SET "ProfilePhotoUrl" = NULL, "ProfilePhotoPath" = NULL, "ProfilePhotoUpdatedAt" = now(), "UpdatedAt" = now()
        FROM old WHERE u."Id" = old."Id"
        RETURNING old."ProfilePhotoPath" AS "OldPath"
        """;

    public const string RemovedTitle = "Your profile photo was removed";
    public const string RemovedBody = "Please add a new one.";

    private sealed class ProfilePhotoClearRow
    {
        public string? OldPath { get; set; }
    }

    public static async Task<IResult> RemoveProfilePhoto(
        Guid id, HttpContext http, IUnitOfWork unitOfWork, ApplicationDbContext db, IPhotoService photoService, IServiceProvider sp,
        IHubContext<NotificationHub> hubContext, IRabbitMqPublisher publisher)
    {
        ProfilePhotoClearRow? cleared;
        NotificationEvent? notification = null;
        await unitOfWork.BeginTransactionAsync();
        try
        {
            cleared = (await db.Database.SqlQueryRaw<ProfilePhotoClearRow>(ClearProfilePhotoSql,
                    new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = id })
                .ToListAsync()).FirstOrDefault();
            if (cleared != null)
            {
                notification = BuildNotice(id);
                db.NotificationEvents.Add(notification);
                await unitOfWork.SaveChangesAsync();
                await unitOfWork.CommitTransactionAsync();
            }
            else await unitOfWork.RollbackTransactionAsync();
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }
        if (cleared == null) return NotFoundResponse("No profile photo");

        await PhotoCleanup.TryDeleteAsync(photoService, cleared.OldPath, PhotoCleanup.Logger(sp));
        await AdminHandlers.SendGoLiveNotificationAsync(hubContext, publisher, notification!);
        AdminActionLog.Write(http, "user.profile_photo.remove", id);
        return NoContentResponse();
    }

    public static NotificationEvent BuildNotice(Guid userId) => new()
    {
        Id = Guid.NewGuid(),
        TargetUserId = userId,
        Type = NotificationTypes.ProfilePhotoRemoved,
        Title = RemovedTitle,
        Body = RemovedBody,
        ActionRoute = UserNotificationRoutes.ProfilePhoto,
        CreatedAt = DateTime.UtcNow,
    };
}
