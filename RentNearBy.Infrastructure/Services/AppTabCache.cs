using Microsoft.Extensions.Caching.Memory;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;

namespace RentNearBy.Infrastructure.Services;

// The one reader of the AppTab table (7 rows), shared by the API (ConfigHandlers, TabGateFilter, Home, roles,
// chat) and the background jobs. Process-local IMemoryCache: correct for the single api instance; a scale-out
// needs a shared version key (e.g. in Redis) so every instance drops its copy on an admin change.
public static class AppTabCache
{
    public const string Key = "config_app_tabs";
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);

    // Bumped on every admin change; a read that started under an older version never writes the cache, so a
    // slow reader holding pre-commit rows cannot overwrite the admin's post-commit snapshot.
    private static int _version;

    public static async Task<List<AppTabDto>> GetAsync(IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        if (cache.TryGetValue(Key, out List<AppTabDto>? cached) && cached != null) return cached;
        return await RefreshAsync(unitOfWork, cache);
    }

    public static async Task<List<AppTabDto>> RefreshAsync(IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        var version = Volatile.Read(ref _version);
        var tabs = await unitOfWork.AppTabs.GetAllAsync();
        var list = tabs.OrderBy(t => t.SortOrder).Select(ToDto).ToList();
        if (version == Volatile.Read(ref _version)) cache.Set(Key, list, Ttl);
        return list;
    }

    public static void Invalidate(IMemoryCache cache)
    {
        Interlocked.Increment(ref _version);
        cache.Remove(Key);
    }

    public static AppTabDto ToDto(AppTab t) => new()
    {
        TabKey = t.TabKey,
        DisplayName = t.DisplayName,
        IsActive = t.IsActive,
        SortOrder = t.SortOrder,
        ApiGated = AppTabKeys.ApiGated.Contains(t.TabKey),
        LinkedRoleKey = AppTabKeys.LinkedRoleKey(t.TabKey),
    };
}
