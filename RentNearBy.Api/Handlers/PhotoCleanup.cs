using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Api.Handlers;

// Best-effort Cloudinary delete for swap paths: a failed delete is logged by path (never the url) so the orphan can be found.
public static class PhotoCleanup
{
    public const string OrphanLog = "photo.orphan {Path}";

    public static async Task TryDeleteAsync(IPhotoService photos, string? path, ILogger? logger)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { await photos.DeletePhotoAsync(path); }
        catch (Exception ex) { logger?.LogWarning(ex, OrphanLog, path); }
    }

    public static ILogger? Logger(IServiceProvider? sp) => sp?.GetService<ILoggerFactory>()?.CreateLogger("Photos");
}
