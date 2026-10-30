using System.Security.Claims;
using System.Security.Cryptography;
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

// Redis keys for the taxi feeds (first page only). Busted on every change that moves a taxi in or
// out of the public set or changes what a card shows. Search keys are not district-bound, so every
// invalidation also drops them.
public static class TaxiCache
{
    public static readonly TimeSpan LocalTtl = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan SearchTtl = TimeSpan.FromSeconds(60);

    public static string LocalKey(Guid districtId, Guid? cityId, string? category, int pageSize)
        => $"taxis:local:{districtId:N}:{cityId:N}:{category}:{pageSize}";

    public static string SearchKey(string? category, double fromLat, double fromLng, double toLat, double toLng, int pageSize)
        => $"taxis:search:{category}:{fromLat:F3}:{fromLng:F3}:{toLat:F3}:{toLng:F3}:{pageSize}";

    public static async Task InvalidateAsync(IConnectionMultiplexer? redis, Guid? districtId)
    {
        if (redis == null) return;
        try
        {
            var db = redis.GetDatabase();
            var server = redis.GetServers().FirstOrDefault(s => s.IsConnected);
            if (server == null) return;
            var patterns = districtId.HasValue
                ? new[] { $"taxis:local:{districtId.Value:N}:*", "taxis:search:*" }
                : new[] { "taxis:search:*" };
            foreach (var pattern in patterns)
                await foreach (var key in server.KeysAsync(pattern: pattern))
                    await db.KeyDeleteAsync(key);
        }
        catch { /* best-effort: TTL (<= 60s) covers Redis failures */ }
    }
}

public static class TaxiHandlers
{
    private static readonly TimeSpan ReportSubmitWindow = TimeSpan.FromHours(1);
    private const int ReportSubmitMax = 5;
    private const int DefaultPageSize = 10;
    private const int MaxPageSize = 30;
    private const int MaxPage = 1000;
    private const string TaxiNotFound = "Taxi not found";

    // Cached first-page shape under TaxiCache keys; HomeHandlers.GetFeed reads the same entries.
    internal sealed record TaxiPage(List<TaxiBrowseDto> Items, bool HasMore);

    private static bool IsPublic(TaxiListing t) => !t.IsDeleted && t.Status == TaxiStatuses.Approved && t.IsAvailable;

    private static IResult LockedResponse() =>
        ConflictResponse("Your taxi is under review.", TaxiErrorTypes.TaxiLocked);

    private static IResult StepLocked(int step) =>
        ConflictResponse($"Complete step {step} first.", TaxiErrorTypes.TaxiStepLocked);

    private static IResult Concurrent() =>
        ConflictResponse("This taxi was just modified by another request. Please retry.", "CONCURRENT_UPDATE");

    private static IResult PlateTaken() =>
        ConflictResponse("This number plate is already listed", TaxiErrorTypes.TaxiPlateTaken);

    private static async Task<TaxiOwnerDto> ToOwnerDtoAsync(TaxiListing taxi, IUnitOfWork unitOfWork)
    {
        var dto = taxi.Adapt<TaxiOwnerDto>();
        var counts = await unitOfWork.ListingReports.GetPendingCountsForListingsAsync(new[] { taxi.Id }, ListingKinds.Taxi);
        dto.PendingReportCount = counts.GetValueOrDefault(taxi.Id);
        return dto;
    }

    private static void RecomputeMinFare(TaxiListing taxi) =>
        taxi.MinFare = taxi.Routes.Count == 0 ? null : taxi.Routes.Min(r => r.Fare);

    // Best effort: the review row is already saved, a lost push only delays the admin.
    private static async Task PublishReviewRequestAsync(IRabbitMqPublisher publisher, Guid taxiId)
    {
        try
        {
            var message = new GoLiveRequestedMessage { ListingId = taxiId, ListingType = ListingKinds.Taxi };
            await publisher.PublishAsync("golive.requested", JsonSerializer.Serialize(message));
        }
        catch { }
    }

    private static void SendBackToReview(TaxiListing taxi, DateTime now)
    {
        taxi.Status = TaxiStatuses.PendingReview;
        taxi.SubmittedAt = now;
        taxi.RejectedReason = null;
    }

    private static IResult CipherNotConfigured() =>
        ErrorResponse(503, "Licence capture is temporarily unavailable", TaxiErrorTypes.IdentityCipherNotConfigured);

    // Normalised plain licence, or null when absent, the key is missing, or the value cannot be decrypted.
    // Only the row id is ever logged, never the value.
    internal static string? ReadLicence(IIdentityCipher cipher, string? stored, Guid id, ILogger logger, bool deletedRecord = false)
    {
        if (stored == null || !cipher.IsConfigured) return null;
        try
        {
            return cipher.Unprotect(stored);
        }
        catch (CryptographicException)
        {
            if (deletedRecord) logger.LogWarning("deleted_account.dl.unreadable {RecordId}", id);
            else logger.LogWarning("taxi.dl.unreadable {TaxiId}", id);
            return null;
        }
    }

    private static (int Page, int Size) ClampPaging(int page, int pageSize) =>
        (Math.Clamp(page, 1, MaxPage), Math.Clamp(pageSize <= 0 ? DefaultPageSize : pageSize, 1, MaxPageSize));

    private static string? NormalizeCategory(string? category) =>
        category != null && TaxiVehicleCategories.All.Contains(category) ? category : null;

    private static async Task<TaxiPage?> ReadPageCacheAsync(IConnectionMultiplexer? redis, string key)
    {
        if (redis == null) return null;
        try
        {
            var cached = await redis.GetDatabase().StringGetAsync(key);
            return cached.HasValue ? JsonSerializer.Deserialize<TaxiPage>(cached!) : null;
        }
        catch { return null; }
    }

    private static async Task WritePageCacheAsync(IConnectionMultiplexer? redis, string key, TaxiPage page, TimeSpan ttl)
    {
        if (redis == null) return;
        try { await redis.GetDatabase().StringSetAsync(key, JsonSerializer.Serialize(page), ttl); } catch { }
    }

    // ---- Public ----

    // Local view: approved, available taxis whose base is in the district, city rows first, cheapest first.
    public static async Task<IResult> GetBrowse(
        Guid districtId, Guid? cityId, string? category,
        IUnitOfWork unitOfWork, IServiceProvider sp, int page = 1, int pageSize = DefaultPageSize)
    {
        if (districtId == Guid.Empty) return BadRequestResponse("District is required");
        var (pageNo, size) = ClampPaging(page, pageSize);
        var categoryFilter = NormalizeCategory(category);
        var redis = pageNo == 1 ? sp.GetService<IConnectionMultiplexer>() : null;
        var cacheKey = TaxiCache.LocalKey(districtId, cityId, categoryFilter, size);

        var result = await ReadPageCacheAsync(redis, cacheKey);
        if (result == null)
        {
            var (items, hasMore) = await unitOfWork.Taxis.BrowseLocalPagedAsync(districtId, cityId, categoryFilter, pageNo, size);
            result = new TaxiPage(items.ToList(), hasMore);
            await WritePageCacheAsync(redis, cacheKey, result, TaxiCache.LocalTtl);
        }

        return OkResponse(new { items = result.Items, hasMore = result.HasMore });
    }

    // Route search: both ends within TaxiLimits.MatchRadiusMeters of a stored route (either direction).
    public static async Task<IResult> GetSearch(
        double? fromLat, double? fromLng, double? toLat, double? toLng, string? category,
        IUnitOfWork unitOfWork, IServiceProvider sp, int page = 1, int pageSize = DefaultPageSize)
    {
        if (fromLat is not (>= -90 and <= 90) || toLat is not (>= -90 and <= 90)
            || fromLng is not (>= -180 and <= 180) || toLng is not (>= -180 and <= 180))
            return BadRequestResponse("Invalid coordinates");

        var (pageNo, size) = ClampPaging(page, pageSize);
        var categoryFilter = NormalizeCategory(category);
        var redis = pageNo == 1 ? sp.GetService<IConnectionMultiplexer>() : null;
        var cacheKey = TaxiCache.SearchKey(categoryFilter, fromLat.Value, fromLng.Value, toLat.Value, toLng.Value, size);

        var result = await ReadPageCacheAsync(redis, cacheKey);
        if (result == null)
        {
            var (items, hasMore) = await unitOfWork.Taxis.SearchByRoutePagedAsync(
                fromLat.Value, fromLng.Value, toLat.Value, toLng.Value, categoryFilter, pageNo, size);
            result = new TaxiPage(items.ToList(), hasMore);
            await WritePageCacheAsync(redis, cacheKey, result, TaxiCache.SearchTtl);
        }

        return OkResponse(new { items = result.Items, hasMore = result.HasMore });
    }

    // Share-link resolver: public taxis only, no contact numbers.
    public static async Task<IResult> GetBySlug(string slug, IUnitOfWork unitOfWork)
    {
        var taxi = await unitOfWork.Taxis.GetBySlugWithAllAsync(slug);
        if (taxi == null) return NotFoundResponse(TaxiNotFound);
        return OkResponse(taxi.Adapt<TaxiPublicSlugDto>());
    }

    // Contact numbers only for logged-in callers. The driver may open his own taxi in any status.
    public static async Task<IResult> GetById(Guid id, IUnitOfWork unitOfWork, ClaimsPrincipal principal)
    {
        var taxi = await unitOfWork.Taxis.GetByIdWithAllAsync(id);
        if (taxi == null) return NotFoundResponse(TaxiNotFound);

        var isAuth = principal.Identity?.IsAuthenticated == true;
        var hasUser = UsersHandlers.TryGetUserId(principal, out var userId);
        var isOwner = isAuth && hasUser && taxi.UserId == userId;
        if (!IsPublic(taxi) && !isOwner) return NotFoundResponse(TaxiNotFound);

        var dto = taxi.Adapt<TaxiPublicDto>();
        if (!isAuth)
        {
            dto.ContactPhone = null;
            dto.WhatsAppNumber = null;
        }
        else if (hasUser)
        {
            dto.HasReported = await unitOfWork.ListingReports.HasPendingReportFromReporterAsync(id, ListingKinds.Taxi, userId);
        }
        return OkResponse(dto);
    }

    public static async Task<IResult> ReportTaxi(
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

        var taxi = await unitOfWork.Taxis.GetByIdWithAllAsync(id);
        if (taxi == null || !IsPublic(taxi)) return NotFoundResponse(TaxiNotFound);
        if (taxi.UserId == userId) return BadRequestResponse("You cannot report your own taxi");

        var reason = await unitOfWork.ReportReasons.GetByIdAsync(request.ReasonId);
        if (reason == null) return BadRequestResponse("Invalid reason");

        var reporter = await unitOfWork.Users.GetByIdAsync(userId);

        var isFirstPending = !await unitOfWork.ListingReports.HasPendingReportForListingAsync(id, ListingKinds.Taxi);

        var report = new ListingReport
        {
            Id = Guid.NewGuid(),
            ListingId = id,
            ListingType = ListingKinds.Taxi,
            ReporterUserId = userId,
            ReporterName = reporter?.Name ?? "Unknown",
            ReporterMobile = reporter?.PhoneNumber ?? "",
            ReportedUserId = taxi.UserId,
            ReportedName = taxi.User?.Name ?? "Unknown",
            ReportedMobile = taxi.User?.PhoneNumber ?? "",
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
            return ConflictResponse("You have already reported this taxi");
        }

        var message = new ReportFiledMessage
        {
            OwnerId = taxi.UserId,
            ListingId = taxi.Id,
            ListingType = ListingKinds.Taxi,
            ReasonName = reason.Name,
            ListingTitle = $"{taxi.VehicleType?.Name ?? "Taxi"} {TaxiPlate.Format(taxi.NumberPlate)}".Trim(),
            NotifyOwner = isFirstPending,
        };
        try { await publisher.PublishAsync("report.filed", JsonSerializer.Serialize(message)); }
        catch (Exception) { /* best-effort — report row is already saved regardless of push delivery */ }

        return CreatedResponse(new { reportId = report.Id }, $"/api/v1/admin/reports/{report.Id}");
    }

    // ---- Driver (CanListTaxis), always keyed on the caller's live taxi ----

    public static async Task<IResult> GetMine(ClaimsPrincipal principal, IUnitOfWork unitOfWork)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var taxi = await unitOfWork.Taxis.GetLiveByUserAsync(userId);
        if (taxi == null) return OkResponse<TaxiOwnerDto?>(null);
        return OkResponse(await ToOwnerDtoAsync(taxi, unitOfWork));
    }

    // Allowed in Draft, Rejected and Approved. On an Approved taxi a new plate, licence number or date of
    // birth sends it back to review (hidden meanwhile); every other field applies at once.
    public static async Task<IResult> UpsertBasic(
        UpsertTaxiBasicRequest request,
        ClaimsPrincipal principal,
        IValidator<UpsertTaxiBasicRequest> validator,
        IUnitOfWork unitOfWork,
        ApplicationDbContext db,
        IRabbitMqPublisher publisher,
        IPlanEntitlementService entitlements,
        IIdentityCipher cipher,
        ILoggerFactory loggerFactory,
        IServiceProvider sp)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var taxi = await unitOfWork.Taxis.GetLiveByUserAsync(userId);
        if (taxi?.Status == TaxiStatuses.PendingReview) return LockedResponse();

        var vehicleType = await unitOfWork.TaxiVehicleTypes.GetByIdAsync(request.VehicleTypeId!.Value);
        if (vehicleType == null || (!vehicleType.IsActive && taxi?.VehicleTypeId != vehicleType.Id))
            return BadRequestResponse("Pick a valid vehicle type");

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

        var point = new NetTopologySuite.Geometries.Point((double)request.Longitude, (double)request.Latitude) { SRID = 4326 };
        var districtId = request.DistrictId;
        var insideDistrict = await db.Districts.AsNoTracking()
            .AnyAsync(d => d.Id == districtId && d.IsActive && d.Boundary != null && d.Boundary.Contains(point));
        if (!insideDistrict)
            return BadRequestResponse("Pin your base inside the selected district", TaxiErrorTypes.TaxiBaseOutsideDistrict);

        var plate = TaxiPlate.Normalize(request.NumberPlate);
        if (await unitOfWork.Taxis.IsPlateTakenAsync(plate, userId)) return PlateTaken();

        // Licence + date of birth: null in the request keeps the stored value; required when nothing usable is stored.
        var dlNew = request.DrivingLicenceNumber == null ? null : TaxiIdentityRules.NormalizeDl(request.DrivingLicenceNumber);
        DateOnly? dobNew = request.DateOfBirth != null && TaxiIdentityRules.TryParseDob(request.DateOfBirth, out var parsedDob)
            ? parsedDob
            : null;
        if (dlNew != null && !cipher.IsConfigured) return CipherNotConfigured();

        var dlStoredEnc = taxi?.DrivingLicenceNumberEnc;
        string? dlStored = null;
        if (dlStoredEnc != null && cipher.IsConfigured)
            dlStored = ReadLicence(cipher, dlStoredEnc, taxi!.Id, loggerFactory.CreateLogger(nameof(TaxiHandlers)));

        string? dlEff;
        if (dlNew != null)
        {
            dlEff = dlNew;
        }
        else if (dlStoredEnc == null || (cipher.IsConfigured && dlStored == null))
        {
            return BadRequestResponse(TaxiIdentityRules.DlRequiredMessage, TaxiErrorTypes.TaxiIdentityRequired);
        }
        else if (!cipher.IsConfigured)
        {
            // The stored licence cannot be read without the key: keep it, but a new date of birth needs the cross-check.
            if (dobNew != null && dobNew != taxi!.DateOfBirth) return CipherNotConfigured();
            dlEff = null;
        }
        else
        {
            dlEff = dlStored;
        }

        var dobEff = dobNew ?? taxi?.DateOfBirth;
        if (dobEff == null)
            return BadRequestResponse(TaxiIdentityRules.DobRequiredMessage, TaxiErrorTypes.TaxiIdentityRequired);
        if (dlEff != null && !TaxiIdentityRules.DlYearMatchesDob(TaxiIdentityRules.DlIssueYear(dlEff), dobEff.Value))
            return BadRequestResponse(TaxiIdentityRules.DlYearMessage, TaxiErrorTypes.TaxiIdentityRequired);

        // A first fill on a legacy row is written but is not a change of what was reviewed.
        var dlWrite = dlNew != null && (dlStoredEnc == null || dlNew != dlStored);
        var dlChanged = dlWrite && dlStoredEnc != null;
        var dobChanged = dobNew != null && taxi?.DateOfBirth != null && dobNew != taxi.DateOfBirth;
        var identityChanged = dlChanged || dobChanged;

        var isNew = taxi == null;
        var now = DateTime.UtcNow;
        if (taxi == null)
        {
            var limits = await entitlements.GetLimitsAsync(userId, ListingKinds.Taxi);
            taxi = new TaxiListing
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Status = TaxiStatuses.Draft,
                IsAvailable = true,
                PriorityRank = limits.PriorityRank,
                IsFeatured = limits.IsFeatured,
                CreatedAt = now,
            };
        }

        var wasApproved = taxi.Status == TaxiStatuses.Approved;
        var plateChanged = taxi.NumberPlate != plate;
        var oldDistrictId = taxi.DistrictId;

        taxi.VehicleTypeId = vehicleType.Id;
        taxi.VehicleType = vehicleType;
        taxi.Seats = request.Seats;
        taxi.NumberPlate = plate;
        taxi.ContactPhone = request.ContactPhone.Trim();
        taxi.WhatsAppNumber = request.WhatsAppNumber.Trim();
        // Never re-encrypt an unchanged licence: the stored blob stays the record of what was reviewed.
        if (dlWrite) taxi.DrivingLicenceNumberEnc = cipher.Protect(dlNew!);
        taxi.DateOfBirth = dobEff;
        taxi.Latitude = request.Latitude;
        taxi.Longitude = request.Longitude;
        taxi.DistrictId = district.Id;
        taxi.CityId = city?.Id;
        taxi.District = district;
        taxi.City = city;
        taxi.UpdatedAt = now;

        var sentBack = wasApproved && (plateChanged || identityChanged);
        if (sentBack) SendBackToReview(taxi, now);

        if (!isNew)
        {
            try { await unitOfWork.SaveChangesAsync(); }
            catch (DbUpdateConcurrencyException) { return Concurrent(); }
            catch (DbUpdateException) { return PlateTaken(); }

            if (wasApproved)
            {
                var redis = sp.GetService<IConnectionMultiplexer>();
                await TaxiCache.InvalidateAsync(redis, taxi.DistrictId);
                if (oldDistrictId != taxi.DistrictId) await TaxiCache.InvalidateAsync(redis, oldDistrictId);
            }
            if (sentBack) await PublishReviewRequestAsync(publisher, taxi.Id);

            return OkResponse(await ToOwnerDtoAsync(taxi, unitOfWork));
        }

        // One live taxi per account and one live taxi per plate are enforced by partial unique
        // indexes; a racing insert fails on SaveChanges and is reported below.
        db.TaxiListings.Add(taxi);
        var baseSlug = SlugGenerator.Generate($"{vehicleType.Name} taxi {city?.Name ?? district.Name}");
        var inserted = await SlugGenerator.GenerateUniqueSlugWithRetryAsync(
            baseSlug,
            slug => taxi.Slug = slug,
            async () =>
            {
                try
                {
                    await unitOfWork.SaveChangesAsync();
                    return true;
                }
                catch (DbUpdateException)
                {
                    return false;
                }
            });
        if (!inserted)
        {
            db.Entry(taxi).State = EntityState.Detached;
            if (await db.TaxiListings.AnyAsync(t => t.UserId == userId && !t.IsDeleted))
                return ConflictResponse("One taxi per account", TaxiErrorTypes.TaxiAlreadyExists);
            if (await unitOfWork.Taxis.IsPlateTakenAsync(plate, userId))
                return PlateTaken();
            return ConflictResponse("Could not generate a unique link for this taxi. Please try again.", "SLUG_GENERATION_FAILED");
        }

        return OkResponse(await ToOwnerDtoAsync(taxi, unitOfWork));
    }

    // The one write allowed under review: the Vehicle page saves basic first and then the photo, so a
    // plate change on an Approved taxi (now PendingReview) must still land its matching photo.
    public static async Task<IResult> UploadPlatePhoto(
        IFormFile photo, ClaimsPrincipal principal, IUnitOfWork unitOfWork, IPhotoService photoService,
        IRabbitMqPublisher publisher, IServiceProvider sp)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var taxi = await unitOfWork.Taxis.GetLiveByUserAsync(userId);
        if (taxi == null) return NotFoundResponse(TaxiNotFound);
        if (photo.Length > 10 * 1024 * 1024) return BadRequestResponse("Photo size must not exceed 10MB");

        using var stream = photo.OpenReadStream();
        if (!IsValidImageMagicBytes(stream)) return BadRequestResponse("File must be a valid image (JPEG, PNG or WebP)");
        stream.Position = 0;
        // Same Cloudinary prefix as the other listings (user_{userId}/listing_{id}), so the shared
        // purge/account-deletion photo cleanup covers it.
        var (url, filePath) = await photoService.SavePhotoAsync(stream, photo.FileName, userId, taxi.Id);

        var oldPath = taxi.PlatePhotoPath;
        var wasApproved = taxi.Status == TaxiStatuses.Approved;
        var now = DateTime.UtcNow;
        taxi.PlatePhotoUrl = url;
        taxi.PlatePhotoPath = filePath;
        taxi.UpdatedAt = now;
        if (wasApproved) SendBackToReview(taxi, now);

        try { await unitOfWork.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException)
        {
            try { await photoService.DeletePhotoAsync(filePath); } catch { }
            return Concurrent();
        }

        if (!string.IsNullOrEmpty(oldPath) && oldPath != filePath)
        {
            try { await photoService.DeletePhotoAsync(oldPath); } catch { /* orphan only */ }
        }

        if (wasApproved)
        {
            await TaxiCache.InvalidateAsync(sp.GetService<IConnectionMultiplexer>(), taxi.DistrictId);
            await PublishReviewRequestAsync(publisher, taxi.Id);
        }

        return CreatedResponse(new { photoUrl = url, status = taxi.Status }, url);
    }

    public static async Task<IResult> UpsertRoutes(
        UpsertTaxiRoutesRequest request,
        ClaimsPrincipal principal,
        IValidator<UpsertTaxiRoutesRequest> validator,
        IUnitOfWork unitOfWork,
        ApplicationDbContext db,
        IServiceProvider sp)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            var error = validation.Errors[0];
            return error.ErrorCode == TaxiErrorTypes.TaxiRouteLimit
                ? BadRequestResponse(error.ErrorMessage, TaxiErrorTypes.TaxiRouteLimit)
                : BadRequestResponse(error.ErrorMessage);
        }

        var taxi = await unitOfWork.Taxis.GetLiveByUserAsync(userId);
        if (taxi == null) return NotFoundResponse(TaxiNotFound);
        if (taxi.Status == TaxiStatuses.PendingReview) return LockedResponse();
        if (!TaxiSetupRules.Step1Done(taxi)) return StepLocked(1);

        // Full replace by position: slot N keeps its row (and RouteIndex N), extra rows are removed,
        // so the (TaxiListingId, RouteIndex) unique index never sees a transient duplicate.
        var now = DateTime.UtcNow;
        var existing = taxi.Routes.OrderBy(r => r.RouteIndex).ToList();
        for (var i = 0; i < request.Routes.Count; i++)
        {
            var input = request.Routes[i];
            var index = (short)(i + 1);
            var route = existing.FirstOrDefault(r => r.RouteIndex == index);
            if (route == null)
            {
                route = new TaxiRoute
                {
                    Id = Guid.NewGuid(),
                    TaxiListingId = taxi.Id,
                    RouteIndex = index,
                    CreatedAt = now,
                };
                db.TaxiRoutes.Add(route);
                taxi.Routes.Add(route);
            }
            route.FromName = input.FromName.Trim();
            route.FromLabel = string.IsNullOrWhiteSpace(input.FromLabel) ? null : input.FromLabel.Trim();
            route.FromLatitude = input.FromLatitude;
            route.FromLongitude = input.FromLongitude;
            route.ToName = input.ToName.Trim();
            route.ToLabel = string.IsNullOrWhiteSpace(input.ToLabel) ? null : input.ToLabel.Trim();
            route.ToLatitude = input.ToLatitude;
            route.ToLongitude = input.ToLongitude;
            route.Fare = input.Fare;
            route.TripType = input.TripType.Trim();
            route.UpdatedAt = now;
        }
        foreach (var extra in existing.Where(r => r.RouteIndex > request.Routes.Count))
        {
            db.TaxiRoutes.Remove(extra);
            taxi.Routes.Remove(extra);
        }

        RecomputeMinFare(taxi);
        taxi.UpdatedAt = now;

        try { await unitOfWork.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException) { return Concurrent(); }

        if (taxi.Status == TaxiStatuses.Approved)
            await TaxiCache.InvalidateAsync(sp.GetService<IConnectionMultiplexer>(), taxi.DistrictId);
        return OkResponse(await ToOwnerDtoAsync(taxi, unitOfWork));
    }

    public static async Task<IResult> UpdateFare(
        Guid routeId,
        UpdateTaxiFareRequest request,
        ClaimsPrincipal principal,
        IValidator<UpdateTaxiFareRequest> validator,
        IUnitOfWork unitOfWork,
        IServiceProvider sp)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var taxi = await unitOfWork.Taxis.GetLiveByUserAsync(userId);
        if (taxi == null) return NotFoundResponse(TaxiNotFound);
        if (taxi.Status == TaxiStatuses.PendingReview) return LockedResponse();

        var route = taxi.Routes.FirstOrDefault(r => r.Id == routeId);
        if (route == null) return NotFoundResponse("Route not found");

        var now = DateTime.UtcNow;
        route.Fare = request.Fare;
        route.UpdatedAt = now;
        RecomputeMinFare(taxi);
        taxi.UpdatedAt = now;

        try { await unitOfWork.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException) { return Concurrent(); }

        if (taxi.Status == TaxiStatuses.Approved)
            await TaxiCache.InvalidateAsync(sp.GetService<IConnectionMultiplexer>(), taxi.DistrictId);
        return OkResponse(await ToOwnerDtoAsync(taxi, unitOfWork));
    }

    public static async Task<IResult> Submit(
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, IRabbitMqPublisher publisher)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var taxi = await unitOfWork.Taxis.GetLiveByUserAsync(userId);
        if (taxi == null) return NotFoundResponse(TaxiNotFound);
        if (taxi.Status == TaxiStatuses.PendingReview) return LockedResponse();
        if (taxi.Status == TaxiStatuses.Approved)
            return ConflictResponse("Your taxi is already live.", TaxiErrorTypes.TaxiLocked);
        if (!TaxiSetupRules.Step1Done(taxi)) return StepLocked(1);
        if (!TaxiSetupRules.RoutesDone(taxi)) return StepLocked(2);
        if (!TaxiSetupRules.IdentityDone(taxi))
            return BadRequestResponse("Add your licence and date of birth", TaxiErrorTypes.TaxiIdentityRequired);

        var now = DateTime.UtcNow;
        SendBackToReview(taxi, now);
        taxi.UpdatedAt = now;

        try { await unitOfWork.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException) { return Concurrent(); }

        await PublishReviewRequestAsync(publisher, taxi.Id);

        return OkResponse(await ToOwnerDtoAsync(taxi, unitOfWork));
    }

    public static async Task<IResult> UpdateAvailability(
        UpdateTaxiAvailabilityRequest request,
        ClaimsPrincipal principal,
        IValidator<UpdateTaxiAvailabilityRequest> validator,
        IUnitOfWork unitOfWork,
        IServiceProvider sp)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var taxi = await unitOfWork.Taxis.GetLiveByUserAsync(userId);
        if (taxi == null) return NotFoundResponse(TaxiNotFound);
        if (taxi.Status == TaxiStatuses.PendingReview) return LockedResponse();
        if (taxi.Status != TaxiStatuses.Approved)
            return ConflictResponse("Your taxi is not live yet.", TaxiErrorTypes.TaxiNotApproved);

        taxi.IsAvailable = request.IsAvailable!.Value;
        taxi.UpdatedAt = DateTime.UtcNow;

        try { await unitOfWork.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException) { return Concurrent(); }

        await TaxiCache.InvalidateAsync(sp.GetService<IConnectionMultiplexer>(), taxi.DistrictId);
        return OkResponse(await ToOwnerDtoAsync(taxi, unitOfWork));
    }

    public static async Task<IResult> DeleteMine(
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, IPhotoService photoService, IServiceProvider sp)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var taxi = await unitOfWork.Taxis.GetLiveByUserAsync(userId);
        if (taxi == null) return NotFoundResponse(TaxiNotFound);

        await photoService.DeleteRoomPhotosAsync(userId, taxi.Id);

        var now = DateTime.UtcNow;
        taxi.IsDeleted = true;
        taxi.DeletedAt = now;
        taxi.UpdatedAt = now;

        try { await unitOfWork.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException) { return Concurrent(); }

        await unitOfWork.ListingReports.AutoResolvePendingForListingAsync(taxi.Id, ListingKinds.Taxi);
        await TaxiCache.InvalidateAsync(sp.GetService<IConnectionMultiplexer>(), taxi.DistrictId);

        return NoContentResponse();
    }

    public static async Task<IResult> GetTaxiReports(
        Guid id, ClaimsPrincipal principal, IUnitOfWork unitOfWork, int page = 1, int pageSize = 20)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var taxi = await unitOfWork.Taxis.GetByIdAsync(id);
        if (taxi == null) return NotFoundResponse(TaxiNotFound);
        if (taxi.UserId != userId) return ForbiddenResponse("You do not own this taxi");

        pageSize = Math.Clamp(pageSize, 1, 50);
        page = Math.Max(1, page);
        var paged = await unitOfWork.ListingReports.GetPagedForListingAsync(id, ListingKinds.Taxi, page, pageSize);
        var items = paged.Items.Select(r => new ListingReportDto
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
        }).ToList();
        return OkResponse(new { items, hasMore = paged.HasMore });
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
}
