using System.Security.Claims;
using System.Text.Json;
using FluentValidation;
using Mapster;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Core.Utils;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services;
using StackExchange.Redis;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

public static class RoomListingsHandlers
{
    private static readonly TimeSpan ReportSubmitWindow = TimeSpan.FromHours(1);
    private const int ReportSubmitMax = 5;

    public static async Task<IResult> GetContext(double lat, double lng, IServiceProvider sp, bool includeAddress = false)
    {
        if (lat < -90 || lat > 90 || lng < -180 || lng > 180)
            return BadRequestResponse("Invalid coordinates");

        var db = sp.GetRequiredService<ApplicationDbContext>();
        var districtId = await LocationContext.FindDistrictIdAsync(db, lat, lng);
        if (districtId is not Guid id)
            return NotFoundResponse("No district found for given coordinates");

        if (!includeAddress)
        {
            var payload = await LocationContext.GetPayloadAsync(db, sp.GetService<IConnectionMultiplexer>(), id);
            if (payload == null) return NotFoundResponse("No district found for given coordinates");
            return OkResponse(LocationContext.Response(payload, LocationContext.NearestCityId(payload, lat, lng)));
        }

        var match = await db.Districts
            .AsNoTracking()
            .Include(d => d.Cities.OrderBy(c => c.Name))
            .FirstOrDefaultAsync(d => d.Id == id);
        if (match == null)
            return NotFoundResponse("No district found for given coordinates");

        var cities = match.Cities.ToList();
        var nearestIndex = GeoCacheKey.NearestIndex(
            cities.Select(c => ((double?)c.Latitude, (double?)c.Longitude)).ToList(), lat, lng);
        var nearestCity = nearestIndex is int n ? cities[n] : cities.FirstOrDefault();

        var cityResolver = sp.GetRequiredService<ICityResolutionService>();
        var (address, resolvedCity) = await cityResolver.ResolveAddressAndCityAsync(match, cities, nearestCity, lat, lng);

        var responseCities = cities;
        if (resolvedCity is not null && cities.All(c => c.Id != resolvedCity.Id))
        {
            responseCities = new List<City>(cities) { resolvedCity };
        }

        return OkResponse(new
        {
            district = new { id = match.Id, name = match.Name, stateName = match.StateName, businessOnboardingEnabled = match.BusinessOnboardingEnabled },
            nearestCityId = resolvedCity?.Id ?? nearestCity?.Id,
            cities = responseCities.Select(c => new { id = c.Id, districtId = c.DistrictId, name = c.Name, latitude = c.Latitude, longitude = c.Longitude }).ToList(),
            address,
        });
    }

    private static readonly TimeSpan NearbyCacheTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan NearestCacheTtl = TimeSpan.FromSeconds(60);

    private static Task BustAsync(IServiceProvider sp, params Guid[] districtIds)
        => sp.GetRequiredService<IListingCacheInvalidator>().BustDistrictsAsync(ListingKinds.Room, districtIds);

    // Read-through of a cell's candidate set under the district generation; the caller refines it per request.
    private static async Task<List<NearbyListingDto>> CellCandidatesAsync(
        IConnectionMultiplexer? redis, Guid districtId, Func<long, string> keyOf, TimeSpan ttl, Func<Task<List<NearbyListingDto>>> load)
    {
        IDatabase? cache = null;
        string? key = null;
        if (redis != null)
        {
            try
            {
                cache = redis.GetDatabase();
                var gen = await ListingCacheGeneration.ReadAsync(cache, ListingCacheKeys.DistrictGen(ListingKinds.Room, districtId));
                key = keyOf(gen);
                var cached = await cache.StringGetAsync(key);
                if (cached.HasValue && JsonSerializer.Deserialize<List<NearbyListingDto>>(cached!) is { } hit) return hit;
            }
            catch { cache = null; }
        }

        var fresh = await load();
        if (cache != null && key != null && ListingVisibility.CacheTtl(ttl, DateTime.UtcNow, fresh.Select(r => r.ValidUntil)) is TimeSpan life)
        {
            try { await cache.StringSetAsync(key, JsonSerializer.Serialize(fresh), life, When.NotExists); } catch { }
        }
        return fresh;
    }

    private static List<NearbyListingDto> Pick(List<NearbyListingDto> candidates, List<GeoHit> hits)
        => hits.Select(h =>
        {
            var row = candidates[h.Index];
            row.DistanceKm = h.DistanceKm;
            return row;
        }).ToList();

    private static List<RentNearBy.Core.Models.GeoPoint> Points(List<NearbyListingDto> rows)
        => rows.Select(r => new RentNearBy.Core.Models.GeoPoint((double)r.Latitude, (double)r.Longitude, r.PriorityRank)).ToList();

    public static async Task<IResult> GetNearby(
        double latitude, double longitude, double radius, Guid districtId,
        IUnitOfWork unitOfWork,
        ClaimsPrincipal principal,
        IServiceProvider sp)
    {
        if (radius < 1 || radius > 50)
            return BadRequestResponse("Radius must be between 1 and 50 km");
        if (latitude < -90 || latitude > 90 || longitude < -180 || longitude > 180)
            return BadRequestResponse("Invalid coordinates");

        var isAuth = principal.Identity?.IsAuthenticated == true;
        var cell = GeoCacheKey.Cell(latitude, longitude);
        var radiusKey = GeoCacheKey.RadiusKey(radius);

        var candidates = await CellCandidatesAsync(
            sp.GetService<IConnectionMultiplexer>(), districtId,
            gen => ListingCacheKeys.Nearby(ListingKinds.Room, districtId, gen, radiusKey, cell), NearbyCacheTtl,
            async () => (await unitOfWork.RoomListings.GetNearbyAsync(
                cell.CenterLat, cell.CenterLng, GeoCacheKey.CandidateRadiusKm(cell, radius), districtId)).ToList());

        var items = Pick(candidates, GeoCacheKey.WithinRadius(Points(candidates), latitude, longitude, radius));
        if (!isAuth) items.ForEach(d => { d.OwnerName = null; d.OwnerPhone = null; });
        return OkResponse(new { items });
    }

    private static readonly TimeSpan NearestRateLimitWindow = TimeSpan.FromMinutes(10);
    private const int NearestRateLimitMax = 20;

    private static string ClientIp(HttpContext httpContext) =>
        httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    public static async Task<IResult> GetNearest(
        double latitude, double longitude, Guid districtId,
        IUnitOfWork unitOfWork,
        ClaimsPrincipal principal,
        IServiceProvider sp,
        IRateLimitService rateLimiter,
        HttpContext httpContext,
        int count = 5)
    {
        if (count < 1 || count > 10)
            return BadRequestResponse("Count must be between 1 and 10");
        if (latitude < -90 || latitude > 90 || longitude < -180 || longitude > 180)
            return BadRequestResponse("Invalid coordinates");

        var ip = ClientIp(httpContext);
        var rl = await rateLimiter.CheckAsync($"nearest:ip:{ip}", NearestRateLimitMax, NearestRateLimitWindow);
        if (!rl.IsAllowed)
        {
            httpContext.Response.Headers["Retry-After"] = ((int)rl.RetryAfter!.Value.TotalSeconds).ToString();
            return TooManyRequestsResponse();
        }

        var isAuth = principal.Identity?.IsAuthenticated == true;
        var cell = GeoCacheKey.Cell(latitude, longitude);

        // Candidates = every row within (k-th nearest from the centre) + 2 x half diagonal: holds the exact
        // k nearest of any point of the cell. Fewer than k rows in the district = all of them.
        var candidates = await CellCandidatesAsync(
            sp.GetService<IConnectionMultiplexer>(), districtId,
            gen => ListingCacheKeys.Nearest(ListingKinds.Room, districtId, gen, count, cell), NearestCacheTtl,
            async () =>
            {
                var knn = (await unitOfWork.RoomListings.GetNearestAsync(cell.CenterLat, cell.CenterLng, count, districtId)).ToList();
                if (knn.Count < count) return knn;
                var kth = GeoCacheKey.MaxDistanceKm(knn.Select(r => ((double)r.Latitude, (double)r.Longitude)), cell.CenterLat, cell.CenterLng);
                return (await unitOfWork.RoomListings.GetNearbyAsync(
                    cell.CenterLat, cell.CenterLng, GeoCacheKey.NearestCandidateRadiusKm(cell, kth), districtId)).ToList();
            });

        var items = Pick(candidates, GeoCacheKey.Nearest(Points(candidates), latitude, longitude, count));
        if (!isAuth) items.ForEach(d => { d.OwnerName = null; d.OwnerPhone = null; });
        return OkResponse(new { items });
    }

    public static async Task<IResult> GetById(Guid id, IUnitOfWork unitOfWork, ClaimsPrincipal principal)
    {
        var listing = await unitOfWork.RoomListings.GetByIdWithPhotosAsync(id);
        if (listing == null) return NotFoundResponse("RoomListing not found");
        var dto = listing.Adapt<RoomListingDto>();
        if (principal.Identity?.IsAuthenticated != true)
        {
            dto.OwnerPhone = null;
        }
        else if (UsersHandlers.TryGetUserId(principal, out var userId))
        {
            dto.HasReported = await unitOfWork.ListingReports.HasPendingReportFromReporterAsync(id, "Room", userId);
        }
        return OkResponse(dto);
    }

    // Anonymous, no-auth lookup for the public website (bakhli.com/l/r/{slug}) and the app's
    // deep-link resolver (/go/r/{slug}) — mirrors GetById's pattern but returns PublicRoomListingDto,
    // which excludes OwnerPhone/UserId/HasReported/PendingReportCount/LiveRequestStatus/RejectedReason
    // at the DTO level, so (unlike GetById) there is no auth-conditional redaction step needed here.
    public static async Task<IResult> GetBySlug(string slug, IUnitOfWork unitOfWork)
    {
        var listing = await unitOfWork.RoomListings.GetBySlugWithPhotosAsync(slug);
        if (listing == null) return NotFoundResponse("RoomListing not found");
        return OkResponse(listing.Adapt<PublicRoomListingDto>());
    }

    public static async Task<IResult> GetMyListings(
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, int page = 1, int pageSize = 10)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();
        if (pageSize < 1 || pageSize > 50) pageSize = 10;
        if (page < 1) page = 1;

        var (items, hasMore) = await unitOfWork.RoomListings.GetByUserIdPagedAsync(userId, page, pageSize);
        var dtos = items.Select(l => l.Adapt<RoomListingDto>()).ToList();
        var counts = await unitOfWork.ListingReports.GetPendingCountsForListingsAsync(dtos.Select(d => d.Id), "Room");
        foreach (var d in dtos) d.PendingReportCount = counts.GetValueOrDefault(d.Id);
        return OkResponse(new { items = dtos, hasMore });
    }

    public static async Task<IResult> GetListingReports(
        Guid id, ClaimsPrincipal principal, IUnitOfWork unitOfWork, int page = 1, int pageSize = 20)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var listing = await unitOfWork.RoomListings.GetByIdAsync(id);
        if (listing == null) return NotFoundResponse("RoomListing not found");
        if (listing.UserId != userId) return ForbiddenResponse("You do not own this listing");

        pageSize = Math.Clamp(pageSize, 1, 50);
        page = Math.Max(1, page);
        var paged = await unitOfWork.ListingReports.GetPagedForListingAsync(id, "Room", page, pageSize);
        var items = paged.Items.Select(ToListingReportDto).ToList();
        return OkResponse(new { items, hasMore = paged.HasMore });
    }

    private static ListingReportDto ToListingReportDto(ListingReport r) => new()
    {
        Id = r.Id,
        ListingId = r.ListingId,
        ListingType = r.ListingType,
        ReasonName = r.Reason?.Name ?? "",
        Details = r.Details,
        Status = r.Status,
        ResolutionAction = r.ResolutionAction,
        CreatedAt = r.CreatedAt,
        ResolvedAt = r.ResolvedAt,
    };

    public static async Task<IResult> CreateListing(
        CreateListingRequest request,
        ClaimsPrincipal principal,
        IValidator<CreateListingRequest> validator,
        IUnitOfWork unitOfWork,
        ILiveSlotService slots,
        IPlanEntitlementService entitlements,
        IServiceProvider sp)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return BadRequestResponse(validation.Errors[0].ErrorMessage);

        // Needed for the slug base (type + locality) below, not just validation — fetched here rather
        // than deferred, since RoomTypeId/DistrictId otherwise never get an existence check today (see
        // CLAUDE.md's "district validation is not enforced server-side" note; this closes that gap for
        // RoomTypeId as a side effect of needing the entity's Name).
        var roomType = await unitOfWork.RoomTypes.GetByIdAsync(request.RoomTypeId);
        if (roomType == null) return BadRequestResponse("Invalid room type");

        var district = await unitOfWork.Districts.GetByIdAsync(request.DistrictId);
        if (district == null) return BadRequestResponse("Invalid district");

        City? city = null;
        if (request.CityId.HasValue)
        {
            city = await unitOfWork.Cities.GetByIdAsync(request.CityId.Value);
            if (city == null) return BadRequestResponse("Selected city does not exist");
            if (city.DistrictId != request.DistrictId)
                return BadRequestResponse("Selected city does not belong to the selected district");
        }

        var listing = new RoomListing
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            RoomTypeId = request.RoomTypeId,
            Description = request.Description,
            PriceMonthly = request.PriceMonthly,
            Latitude = request.Latitude,
            Longitude = request.Longitude,
            Address = request.Address,
            DistrictId = request.DistrictId,
            CityId = request.CityId,
            FurnishedStatus = request.FurnishedStatus,
            IsAc = request.IsAc,
            SecurityDeposit = request.SecurityDeposit,
            Floor = request.Floor,
            IsActive = false, // always — POST /{id}/go-live is now the only path to activation
            CreatedAt = DateTime.UtcNow,
            PublishedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        // Slug generated once here, immutable for the listing's lifetime (see the field comment on
        // RoomListing.Slug) — base is type + locality only, never price/status. Uniqueness is handled
        // by SlugGenerator.GenerateUniqueSlugWithRetryAsync (shared with PlotListingHandlers.CreatePlotListing).
        var baseSlug = SlugGenerator.Generate($"{roomType.Name} {city?.Name ?? district.Name}");
        // The plan limit (used = not deleted, not Rejected) is checked under the owner lock in the same transaction.
        await unitOfWork.RoomListings.AddAsync(listing);
        var outcome = await ListingSlotHelpers.InsertWithinLimitAsync(
            unitOfWork, slots, entitlements, userId, ListingKinds.Room, baseSlug,
            slug => listing.Slug = slug,
            limits =>
            {
                listing.PriorityRank = limits.PriorityRank;
                listing.IsFeatured = limits.IsFeatured;
            });
        if (outcome.Limit is { } limit)
            return GoLiveHandlers.ListingLimitReached(limit, outcome.Used ?? limit);
        if (!outcome.Inserted)
            return ConflictResponse("Could not generate a unique link for this listing. Please try again.", "SLUG_GENERATION_FAILED");

        await BustAsync(sp, listing.DistrictId);

        return CreatedResponse(new { listingId = listing.Id }, $"/api/v1/listings/{listing.Id}");
    }

    public static async Task<IResult> UpdateListing(Guid id, UpdateListingRequest request, ClaimsPrincipal principal, IValidator<UpdateListingRequest> validator, IUnitOfWork unitOfWork, ILiveSlotService slots, IServiceProvider sp)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var listing = await unitOfWork.RoomListings.GetByIdAsync(id);
        if (listing == null) return NotFoundResponse("RoomListing not found");
        if (listing.UserId != userId) return ForbiddenResponse("You do not own this listing");

        var oldDistrictId = listing.DistrictId;

        if (request.RoomTypeId.HasValue) listing.RoomTypeId = request.RoomTypeId.Value;
        if (request.Description != null) listing.Description = request.Description;
        if (request.PriceMonthly.HasValue) listing.PriceMonthly = request.PriceMonthly.Value;
        if (request.Latitude.HasValue) listing.Latitude = request.Latitude.Value;
        if (request.Longitude.HasValue) listing.Longitude = request.Longitude.Value;
        if (request.Address != null) listing.Address = request.Address;
        if (request.FurnishedStatus != null) listing.FurnishedStatus = request.FurnishedStatus;
        if (request.IsAc.HasValue) listing.IsAc = request.IsAc;
        if (request.SecurityDeposit.HasValue) listing.SecurityDeposit = request.SecurityDeposit;
        if (request.Floor.HasValue) listing.Floor = request.Floor;
        if (request.CityId.HasValue)
        {
            var city = await unitOfWork.Cities.GetByIdAsync(request.CityId.Value);
            if (city == null) return BadRequestResponse("Selected city does not exist");
            listing.CityId = request.CityId.Value;
            listing.DistrictId = city.DistrictId;
        }
        var freesSlot = false;
        if (request.IsActive.HasValue)
        {
            if (request.IsActive.Value)
                return BadRequestResponse("Use POST /listings/{id}/go-live to activate a listing.");
            if (listing.IsActive)
            {
                // Owner pause: never auto-resumed; Go Live within the limit brings it back. Its slot goes to
                // the newest plan-paused row.
                freesSlot = listing.LiveRequestStatus != GoLiveRequestStatuses.Pending;
                listing.IsActive = false;
                listing.PauseReason = PauseReasons.Owner;
            }
        }
        listing.UpdatedAt = DateTime.UtcNow;

        await unitOfWork.RoomListings.UpdateAsync(listing);
        var resumed = 0;
        if (freesSlot)
        {
            await unitOfWork.BeginTransactionAsync();
            try
            {
                await slots.LockOwnerAsync(userId);
                try
                {
                    await unitOfWork.SaveChangesAsync();
                }
                catch (DbUpdateConcurrencyException)
                {
                    await unitOfWork.RollbackTransactionAsync();
                    return ConflictResponse("This listing was just modified by another request. Please retry.", "CONCURRENT_UPDATE");
                }
                resumed = await slots.ResumeAfterSlotFreedAsync(userId, ListingKinds.Room);
                await unitOfWork.CommitTransactionAsync();
            }
            catch
            {
                await unitOfWork.RollbackTransactionAsync();
                throw;
            }
        }
        else
        {
            try
            {
                await unitOfWork.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                return ConflictResponse("This listing was just modified by another request. Please retry.", "CONCURRENT_UPDATE");
            }
        }

        if (request.IsActive.HasValue && request.IsActive.Value == false)
            await unitOfWork.ListingReports.AutoResolvePendingForListingAsync(id, "Room");

        await BustAsync(sp, listing.DistrictId, oldDistrictId);
        if (resumed > 0)
            await sp.GetRequiredService<IListingCacheInvalidator>().BustForUserAsync(userId, ListingKinds.Room);

        return OkResponse(new { success = true });
    }

    public static async Task<IResult> DeleteListing(
        Guid id, ClaimsPrincipal principal, IUnitOfWork unitOfWork, IPhotoService photoService, ILiveSlotService slots, IServiceProvider sp)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var listing = await unitOfWork.RoomListings.GetByIdAsync(id);
        if (listing == null) return NotFoundResponse("RoomListing not found");
        if (listing.UserId != userId) return ForbiddenResponse("You do not own this listing");

        var districtId = listing.DistrictId;

        await photoService.DeleteRoomPhotosAsync(userId, id);

        // Deleting a live (or pending) row frees a slot: the newest plan-paused row resumes in the same transaction.
        var resumed = 0;
        await unitOfWork.BeginTransactionAsync();
        try
        {
            await slots.LockOwnerAsync(userId);
            var wasLive = listing.IsActive || listing.LiveRequestStatus == GoLiveRequestStatuses.Pending;
            listing.IsDeleted = true;
            listing.DeletedAt = DateTime.UtcNow;
            await unitOfWork.RoomListings.UpdateAsync(listing);
            try
            {
                await unitOfWork.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                await unitOfWork.RollbackTransactionAsync();
                return ConflictResponse("This listing was just modified by another request. Please retry.", "CONCURRENT_UPDATE");
            }
            if (wasLive)
                resumed = await slots.ResumeAfterSlotFreedAsync(userId, ListingKinds.Room);
            await unitOfWork.CommitTransactionAsync();
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }

        await unitOfWork.ListingReports.AutoResolvePendingForListingAsync(id, "Room");

        await BustAsync(sp, districtId);
        if (resumed > 0)
            await sp.GetRequiredService<IListingCacheInvalidator>().BustForUserAsync(userId, ListingKinds.Room);

        return NoContentResponse();
    }

    // Makes a paused, approved listing live in place of a live one (owner lock, both rows xmin-guarded).
    public static async Task<IResult> SwapLive(
        Guid id,
        SwapLiveRequest request,
        ClaimsPrincipal principal,
        IUnitOfWork unitOfWork,
        ILiveSlotService slots,
        IPlanEntitlementService entitlements,
        IListingCacheInvalidator cacheInvalidator)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();
        if (request == null || request.PauseListingId == Guid.Empty || request.PauseListingId == id)
            return BadRequestResponse("Choose a live listing to pause.", PlanErrorTypes.SwapInvalid);

        int limit, live;
        var districts = new List<Guid>();
        await unitOfWork.BeginTransactionAsync();
        try
        {
            await slots.LockOwnerAsync(userId);
            var target = await unitOfWork.RoomListings.GetByIdAsync(id);
            var other = await unitOfWork.RoomListings.GetByIdAsync(request.PauseListingId);
            if (target == null || other == null || target.IsDeleted || other.IsDeleted
                || target.UserId != userId || other.UserId != userId
                || target.IsActive || target.LiveRequestStatus != GoLiveRequestStatuses.Approved
                || target.PauseReason is PauseReasons.Admin or PauseReasons.Suspended
                || !other.IsActive)
            {
                await unitOfWork.RollbackTransactionAsync();
                return BadRequestResponse("These listings cannot be swapped.", PlanErrorTypes.SwapInvalid);
            }

            var limits = await entitlements.GetLimitsAsync(userId, ListingKinds.Room);
            // Swap only at the limit: with a free slot, plan-pausing `other` would strand it next to an empty slot.
            if (await slots.CountLiveAsync(userId, ListingKinds.Room, excludeId: target.Id) < limits.ListingLimit)
            {
                await unitOfWork.RollbackTransactionAsync();
                return BadRequestResponse("A slot is free. Use Go Live instead.", PlanErrorTypes.SwapInvalid);
            }
            var now = DateTime.UtcNow;
            other.IsActive = false;
            other.PauseReason = PauseReasons.Plan;
            other.UpdatedAt = now;
            target.IsActive = true;
            target.PauseReason = null;
            target.PublishedAt = ListingPublishRules.PublishedAtOnActivation(target.PublishedAt, target.ValidUntil, now);
            target.ValidUntil = entitlements.ResolveValidUntil(limits);
            target.PriorityRank = limits.PriorityRank;
            target.IsFeatured = limits.IsFeatured;
            target.UpdatedAt = now;
            await unitOfWork.RoomListings.UpdateAsync(other);
            await unitOfWork.RoomListings.UpdateAsync(target);
            try
            {
                await unitOfWork.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                await unitOfWork.RollbackTransactionAsync();
                return ConflictResponse("This listing was just modified by another request. Please retry.", "CONCURRENT_UPDATE");
            }
            limit = limits.ListingLimit;
            live = await slots.CountLiveAsync(userId, ListingKinds.Room);
            await unitOfWork.CommitTransactionAsync();
            districts.Add(target.DistrictId);
            districts.Add(other.DistrictId);
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }

        await cacheInvalidator.BustDistrictsAsync(ListingKinds.Room, districts);
        return OkResponse(new { limits = new LiveLimitsDto { ListingLimit = limit, Live = live } });
    }

    public static async Task<IResult> ReportListing(
        Guid id, CreateListingReportRequest request,
        ClaimsPrincipal principal,
        IValidator<CreateListingReportRequest> validator,
        IUnitOfWork unitOfWork, IRabbitMqPublisher publisher,
        IRateLimitService rateLimiter, HttpContext httpContext)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var reportRl = await rateLimiter.CheckAsync($"report:submit:{userId}", ReportSubmitMax, ReportSubmitWindow);
        if (!reportRl.IsAllowed)
        {
            httpContext.Response.Headers["Retry-After"] = ((int)reportRl.RetryAfter!.Value.TotalSeconds).ToString();
            return TooManyRequestsResponse();
        }

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var listing = await unitOfWork.RoomListings.GetByIdAsync(id);
        if (listing == null) return NotFoundResponse("RoomListing not found");
        if (listing.UserId == userId) return BadRequestResponse("You cannot report your own listing");

        var reason = await unitOfWork.ReportReasons.GetByIdAsync(request.ReasonId);
        if (reason == null) return BadRequestResponse("Invalid reason");

        var reporter = await unitOfWork.Users.GetByIdAsync(userId);
        var owner = await unitOfWork.Users.GetByIdAsync(listing.UserId);

        var isFirstPending = !await unitOfWork.ListingReports.HasPendingReportForListingAsync(id, "Room");

        var report = new ListingReport
        {
            Id = Guid.NewGuid(),
            ListingId = id,
            ListingType = "Room",
            ReporterUserId = userId,
            ReporterName = reporter?.Name ?? "Unknown",
            ReporterMobile = reporter?.PhoneNumber ?? "",
            ReportedUserId = listing.UserId,
            ReportedName = owner?.Name ?? "Unknown",
            ReportedMobile = owner?.PhoneNumber ?? "",
            ReasonId = request.ReasonId,
            Details = request.Details,
            Status = "Pending",
            CreatedAt = DateTime.UtcNow,
        };

        await unitOfWork.ListingReports.AddAsync(report);
        try
        {
            await unitOfWork.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            return ConflictResponse("You have already reported this listing");
        }

        var message = new ReportFiledMessage
        {
            OwnerId = listing.UserId,
            ListingId = listing.Id,
            ListingType = "Room",
            ReasonName = reason.Name,
            ListingTitle = listing.Address ?? "your listing",
            NotifyOwner = isFirstPending,
        };
        try { await publisher.PublishAsync("report.filed", JsonSerializer.Serialize(message)); }
        catch (Exception) { /* best-effort — report row is already saved regardless of push delivery */ }

        return CreatedResponse(new { reportId = report.Id }, $"/api/v1/admin/reports/{report.Id}");
    }

    public static async Task<IResult> UploadPhoto(Guid id, IFormFile photo, ClaimsPrincipal principal, IUnitOfWork unitOfWork, IPhotoService photoService, IServiceProvider sp)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var listing = await unitOfWork.RoomListings.GetByIdWithPhotosAsync(id);
        if (listing == null) return NotFoundResponse("RoomListing not found");
        if (listing.UserId != userId) return ForbiddenResponse("You do not own this listing");
        if (listing.Photos.Count >= 5) return BadRequestResponse("Maximum 5 photos allowed per listing");
        if (photo.Length > 10 * 1024 * 1024) return BadRequestResponse("Photo size must not exceed 10MB");

        using var stream = photo.OpenReadStream();
        if (!IsValidImageMagicBytes(stream)) return BadRequestResponse("File must be a valid image (JPEG, PNG or WebP)");
        stream.Position = 0;
        var (url, filePath) = await photoService.SavePhotoAsync(stream, photo.FileName, userId, id);

        var listingPhoto = new RoomPhoto
        {
            Id = Guid.NewGuid(),
            RoomListingId = id,
            PhotoUrl = url,
            FilePath = filePath,
            PhotoOrder = listing.Photos.Count,
            UploadedAt = DateTime.UtcNow
        };

        await unitOfWork.RoomListings.AddPhotoAsync(listingPhoto);
        await unitOfWork.SaveChangesAsync();

        await BustAsync(sp, listing.DistrictId);

        return CreatedResponse(new { photoUrl = url, photoId = listingPhoto.Id }, url);
    }

    private static bool IsValidImageMagicBytes(Stream stream)
    {
        var header = new byte[12];
        var read = stream.Read(header, 0, header.Length);
        if (read < 3) return false;
        if (header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF) return true;
        if (read >= 8 && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47) return true;
        if (read >= 12 && header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46
            && header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50) return true;
        return false;
    }

    public static async Task<IResult> DeletePhoto(Guid id, Guid photoId, ClaimsPrincipal principal, IUnitOfWork unitOfWork, IPhotoService photoService, IServiceProvider sp)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var listing = await unitOfWork.RoomListings.GetByIdWithPhotosAsync(id);
        if (listing == null) return NotFoundResponse("RoomListing not found");
        if (listing.UserId != userId) return ForbiddenResponse("You do not own this listing");

        var photo = listing.Photos.FirstOrDefault(p => p.Id == photoId);
        if (photo == null) return NotFoundResponse("Photo not found");

        await photoService.DeletePhotoAsync(photo.FilePath);
        unitOfWork.RoomListings.RemovePhoto(photo);
        await unitOfWork.SaveChangesAsync();

        await BustAsync(sp, listing.DistrictId);

        return NoContentResponse();
    }
}
