using System.Diagnostics;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using FluentValidation;
using RentNearBy.Api.Validators;
using Mapster;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Core.Interfaces.Owner;
using RentNearBy.Core.Utils;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Infrastructure.Services.Owner;
using RentNearBy.Infrastructure.Services.Owner.Be3;
using RentNearBy.Infrastructure.Services.TaxiRequests;
using StackExchange.Redis;
using RentNearBy.Api.Handlers.Owner;
using RentNearBy.Api.Handlers.Photos;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

// Redis keys for the taxi feeds (first page only). Busted on every change that moves a taxi in or
// out of the public set or changes what a card shows, by generation INCRs (FeedCache): the district's local
// entries, and the search entries (not district-bound) on every invalidation.
public static class TaxiCache
{
    public static readonly TimeSpan LocalTtl = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan SearchTtl = TimeSpan.FromSeconds(60);

    public static string LocalKey(Guid districtId, Guid? cityId, string? category, int pageSize, bool cityOnly = false, bool localRate = false,
        TaxiLocalNeed? need = null)
        => $"taxis:local:{districtId:N}:{cityId:N}:{category}:{pageSize}{(cityOnly ? ":city" : "")}{(localRate ? ":rate" : "")}{NeedPart(need)}";

    // One entry per city (every taxi of the city, ranked per caller); the version moves on any stay or taxi change.
    public static string CityKey(long cityKey, bool localRate) => $"taxis:city:{cityKey}{(localRate ? ":rate" : "")}";
    public static readonly TimeSpan CityTtl = TimeSpan.FromSeconds(60);

    // Pickup km and its tie-break come from the exact start point, so the point is keyed exactly, never rounded.
    public static string NeedPart(TaxiLocalNeed? need)
    {
        if (need is not { IsEmpty: false }) return "";
        var point = need.HasPoint ? $"{Exact(need.Lat!.Value)},{Exact(need.Lng!.Value)}" : "";
        var radius = need.PickupLimitKm is { } r ? Exact(r) : "";
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $":need:{need.People}|{need.DayKm}|{point}|{radius}");
    }

    // Both-ends match is a 5 km test on the exact points: a rounded key would hand one caller's set to another.
    public static string SearchKey(string? category, double fromLat, double fromLng, double toLat, double toLng, int pageSize)
        => $"taxis:search:{category}:{Exact(fromLat)}:{Exact(fromLng)}:{Exact(toLat)}:{Exact(toLng)}:{pageSize}";

    private static string Exact(double value) => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    public static string GenKey(Guid districtId) => ListingCacheKeys.TaxiGen(districtId);
    public const string SearchGenKey = ListingCacheKeys.TaxiSearchGen;

    public static async Task InvalidateAsync(IConnectionMultiplexer? redis, Guid? districtId)
    {
        await RentNearBy.Infrastructure.Services.TripSupplyCache.BumpAsync(redis, districtId);
        await TaxiWaysVersion.BumpAsync(redis);
        await FeedCache.BumpAsync(redis, districtId is { } id ? new[] { GenKey(id), SearchGenKey } : new[] { SearchGenKey });
        await TaxiPickupPlaces.BumpAsync(redis);
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
    private const string PhotoTooBig = "Photo size must not exceed 10MB";
    private const string PhotoNotImage = "File must be a valid image (JPEG, PNG or WebP)";

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

    // Route city names: GetMine and UpsertRoutes (the responses that show routes) wait up to a budget; every other
    // owner response takes only names a warm city grid has at once (zero budget) and never waits.
    private static async Task<TaxiOwnerDto> ToOwnerDtoAsync(TaxiListing taxi, IUnitOfWork unitOfWork, ICityResolver resolver, TimeSpan? namesBudget = null)
    {
        var dto = taxi.Adapt<TaxiOwnerDto>();
        var counts = await unitOfWork.ListingReports.GetPendingCountsForListingsAsync(new[] { taxi.Id }, ListingKinds.Taxi);
        dto.PendingReportCount = counts.GetValueOrDefault(taxi.Id);
        await TaxiRouteCityKeys.FillNamesAsync(dto.Routes, resolver, namesBudget ?? TimeSpan.Zero);
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

    private static async Task<(TaxiPage? Page, long? Generation)> ReadPageCacheAsync(IConnectionMultiplexer? redis, string key, string genKey)
    {
        var (cached, generation) = await FeedCache.ReadAsync(redis, key, genKey);
        if (cached == null) return (null, generation);
        try { return (JsonSerializer.Deserialize<TaxiPage>(cached), generation); }
        catch (JsonException) { return (null, generation); }
    }

    private static Task WritePageCacheAsync(IConnectionMultiplexer? redis, string key, long? generation, TaxiPage page, TimeSpan ttl)
        => FeedCache.WriteAsync(redis, key, generation, JsonSerializer.Serialize(page), ttl);

    // ---- Public ----

    // Local view: approved, available taxis whose base is in the district, city rows first, cheapest first.
    // With cityKey: the taxis whose own nearest city is that city (any district); districtId, cityId, cityOnly and radiusKm are
    // ignored, lat/lng only rank by pickup distance.
    public static async Task<IResult> GetBrowse(
        Guid? districtId, Guid? cityId, string? category, long? cityKey,
        IUnitOfWork unitOfWork, ICityResolver resolver, IServiceProvider sp, int page = 1, int pageSize = DefaultPageSize, bool cityOnly = false,
        bool localRate = false, int? people = null, int? dayKm = null, double? lat = null, double? lng = null, double? radiusKm = null)
    {
        if (cityKey == null && (districtId is null || districtId == Guid.Empty)) return BadRequestResponse("District is required");
        if (TaxiLocalNeedRules.Error(people, dayKm, lat, lng, cityKey != null ? null : radiusKm) is { } needError)
            return BadRequestResponse(needError);
        var need = TaxiLocalNeedRules.Build(people, dayKm, lat, lng, cityKey != null ? null : radiusKm);
        var (pageNo, size) = ClampPaging(page, pageSize);
        if (cityKey != null) return await BrowseCityAsync(cityKey.Value, category, localRate, need, pageNo, size, unitOfWork, resolver, sp);
        var districtGuid = districtId!.Value;
        var categoryFilter = NormalizeCategory(category);
        var cityOnlyFilter = cityOnly && cityId.HasValue;
        var redis = pageNo == 1 ? sp.GetService<IConnectionMultiplexer>() : null;
        var cacheKey = TaxiCache.LocalKey(districtGuid, cityId, categoryFilter, size, cityOnlyFilter, localRate, need);

        var (result, generation) = await ReadPageCacheAsync(redis, cacheKey, TaxiCache.GenKey(districtGuid));
        if (result == null)
        {
            var (items, hasMore) = await unitOfWork.Taxis.BrowseLocalPagedAsync(districtGuid, cityId, categoryFilter, pageNo, size, cityOnlyFilter, localRate, need);
            result = new TaxiPage(items.ToList(), hasMore);
            await WritePageCacheAsync(redis, cacheKey, generation, result, TaxiCache.LocalTtl);
        }

        return OkResponse(new { items = result.Items, hasMore = result.HasMore });
    }

    private static async Task<IResult> BrowseCityAsync(
        long cityKey, string? category, bool localRate, TaxiLocalNeed? need, int pageNo, int size,
        IUnitOfWork unitOfWork, ICityResolver resolver, IServiceProvider sp)
    {
        var pin = await resolver.FindAsync(cityKey);
        if (!pin.Available) return ErrorResponse(503, "Cities are not available right now", TripErrorTypes.CitiesUnavailable);
        if (pin.City is not { } city) return OkResponse(new { items = new List<TaxiBrowseDto>(), hasMore = false });

        var redis = sp.GetService<IConnectionMultiplexer>();
        var cacheKey = TaxiCache.CityKey(cityKey, localRate);
        var (cached, generation) = await FeedCache.ReadAsync(redis, cacheKey, RentNearBy.Infrastructure.Services.TripSupplyCache.AllGenKey);
        List<TaxiAt>? all = null;
        if (cached != null)
        {
            try { all = JsonSerializer.Deserialize<List<TaxiAt>>(cached); }
            catch (JsonException) { all = null; }
        }
        if (all == null)
        {
            var fetch = await CityListings.TaxisAsync(unitOfWork.Taxis, resolver, city, localRate);
            if (fetch.Capped)
                sp.GetService<ILoggerFactory>()?.CreateLogger("TaxiCity").LogWarning("taxis.city.capped {CityKey} {Max}", cityKey, CityListings.MaxCityFetch);
            all = fetch.Items.ToList();
            await FeedCache.WriteAsync(redis, cacheKey, generation, JsonSerializer.Serialize(all), TaxiCache.CityTtl);
        }

        var (items, hasMore) = TaxiCityList.Page(all, NormalizeCategory(category), need, pageNo, size);
        return OkResponse(new { items, hasMore });
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

        var (result, generation) = await ReadPageCacheAsync(redis, cacheKey, TaxiCache.SearchGenKey);
        if (result == null)
        {
            var (items, hasMore) = await unitOfWork.Taxis.SearchByRoutePagedAsync(
                fromLat.Value, fromLng.Value, toLat.Value, toLng.Value, categoryFilter, pageNo, size);
            result = new TaxiPage(items.ToList(), hasMore);
            await WritePageCacheAsync(redis, cacheKey, generation, result, TaxiCache.SearchTtl);
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
        var tripEnquiryId = await TripLifecycleHandlers.OwnTripEnquiryIdAsync(unitOfWork, request.TripEnquiryId, id, userId);

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
            TripEnquiryId = tripEnquiryId,
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

    // M2 (ungated, contract R-5): the driver's own taxi with the hub extras (hidden routes, meters, bookings, ring weight).
    public static async Task<IResult> GetMine(ClaimsPrincipal principal, IUnitOfWork unitOfWork, ICityResolver resolver, TaxiHubReader hub)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var taxi = await unitOfWork.Taxis.GetLiveByUserAsync(userId);
        if (taxi == null) return OkResponse<TaxiOwnerDto?>(null);
        var started = Stopwatch.GetTimestamp();
        var dto = await ToOwnerDtoAsync(taxi, unitOfWork, resolver, TaxiRouteCityKeys.RequestBudget);
        var left = TaxiRouteCityKeys.RequestBudget - Stopwatch.GetElapsedTime(started);
        await hub.FillAsync(dto, taxi, resolver, left > TimeSpan.Zero ? left : TimeSpan.Zero);
        return OkResponse(dto);
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
        ICityResolver resolver,
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

        var check = BusinessOnboardingRules.ForSave(
            await BusinessOnboardingGate.CheckAsync(db, request.DistrictId, request.Latitude, request.Longitude),
            taxi?.EverApproved ?? false, taxi?.DistrictId, request.DistrictId, taxi?.Latitude, taxi?.Longitude, request.Latitude, request.Longitude);
        if (BusinessOnboardingRules.IsOutsideDistrict(check))
            return BadRequestResponse("Pin your base inside the selected district", TaxiErrorTypes.TaxiBaseOutsideDistrict);
        if (check == OnboardingCheck.OnboardingClosed)
            return BadRequestResponse(BusinessOnboardingRules.OnboardingClosedMessage, BusinessOnboardingRules.OnboardingClosedType);

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

        DateOnly dobEff;
        if (dobNew is { } dobGiven) dobEff = dobGiven;
        else if (taxi != null) dobEff = taxi.DateOfBirth;
        else return BadRequestResponse(TaxiIdentityRules.DobRequiredMessage, TaxiErrorTypes.TaxiIdentityRequired);
        if (dlEff != null && !TaxiIdentityRules.DlYearMatchesDob(TaxiIdentityRules.DlIssueYear(dlEff), dobEff))
            return BadRequestResponse(TaxiIdentityRules.DlYearMessage, TaxiErrorTypes.TaxiIdentityRequired);

        // Never re-encrypt an unchanged licence: the stored blob stays the record of what was reviewed.
        var dlEncNew = dlNew != null && (dlStoredEnc == null || dlNew != dlStored) ? cipher.Protect(dlNew) : null;
        var dlChanged = dlEncNew != null && taxi != null;
        var dobChanged = dobNew != null && taxi != null && dobNew != taxi.DateOfBirth;
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
                IsAvailable = ListingSwitchRules.TaxiAvailableOnCreate,
                PriorityRank = limits.PriorityRank,
                IsFeatured = limits.IsFeatured,
                RingWeight = (short)Math.Clamp(limits.RingWeight, 1, 5),
                DrivingLicenceNumberEnc = dlEncNew!,
                DateOfBirth = dobEff,
                CreatedAt = now,
            };
        }

        var wasApproved = taxi.Status == TaxiStatuses.Approved;
        var plateChanged = taxi.NumberPlate != plate;
        // The locked proof shows this plate; a different vehicle is a new taxi (delete and add), never a new plate on this row.
        if (!isNew && plateChanged && TaxiPhotoRules.ProofLocked(taxi))
            return ConflictResponse(TaxiPhotoRules.ProofLockedMessage, TaxiErrorTypes.TaxiProofLocked);
        var oldDistrictId = taxi.DistrictId;

        taxi.VehicleTypeId = vehicleType.Id;
        taxi.VehicleType = vehicleType;
        taxi.Seats = request.Seats;
        taxi.NumberPlate = plate;
        taxi.ContactPhone = request.ContactPhone.Trim();
        taxi.WhatsAppNumber = request.WhatsAppNumber.Trim();
        if (dlEncNew != null) taxi.DrivingLicenceNumberEnc = dlEncNew;
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
            // AU-6: back to review closes his ringing offers (ListingInReview) in the same transaction, before the listing row is
            // written (lock order request -> driver listing); accepted, selected and bookings stay.
            var closure = sentBack ? sp.GetRequiredService<TaxiRequestClosure>() : null;
            var closed = TaxiClosureResult.Empty;
            await unitOfWork.BeginTransactionAsync();
            try
            {
                if (closure != null) closed = await closure.ListingInReviewAsync(db, taxi.Id);
                await unitOfWork.SaveChangesAsync();
                await unitOfWork.CommitTransactionAsync();
            }
            catch (DbUpdateConcurrencyException) { await unitOfWork.RollbackTransactionAsync(); return Concurrent(); }
            catch (Exception ex) when (PgRetry.IsRetryable(ex)) { await unitOfWork.RollbackTransactionAsync(); return Concurrent(); }
            catch (DbUpdateException) { await unitOfWork.RollbackTransactionAsync(); return PlateTaken(); }
            catch { await unitOfWork.RollbackTransactionAsync(); throw; }
            if (closure != null) await closure.AfterCommitAsync(closed, poolChanged: true);

            if (wasApproved)
            {
                var redis = sp.GetService<IConnectionMultiplexer>();
                await TaxiCache.InvalidateAsync(redis, taxi.DistrictId);
                if (oldDistrictId != taxi.DistrictId) await TaxiCache.InvalidateAsync(redis, oldDistrictId);
            }
            if (sentBack) await PublishReviewRequestAsync(publisher, taxi.Id);

            return OkResponse(await ToOwnerDtoAsync(taxi, unitOfWork, resolver));
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

        return OkResponse(await ToOwnerDtoAsync(taxi, unitOfWork, resolver));
    }

    // Proof photo: replaceable in Draft/Rejected until the first approval, frozen under review (the admin approves what he saw),
    // locked for ever after (409 TaxiProofLocked).
    public static async Task<IResult> UploadPlatePhoto(
        IFormFile photo, ClaimsPrincipal principal, IUnitOfWork unitOfWork, IPhotoService photoService, ILoggerFactory? loggerFactory = null)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var taxi = await unitOfWork.Taxis.GetLiveByUserAsync(userId);
        if (taxi == null) return NotFoundResponse(TaxiNotFound);
        if (TaxiPhotoRules.ProofLocked(taxi))
            return ConflictResponse(TaxiPhotoRules.ProofLockedMessage, TaxiErrorTypes.TaxiProofLocked);
        if (taxi.Status == TaxiStatuses.PendingReview) return LockedResponse();
        if (PhotoUpload.Check(photo, PhotoNotImage, PhotoTooBig) is { } bad) return bad;

        using var stream = photo.OpenReadStream();
        var logger = loggerFactory?.CreateLogger("Photos");
        // Same Cloudinary prefix as the other listings (user_{userId}/listing_{id}), so the shared
        // purge/account-deletion photo cleanup covers it.
        var (url, filePath) = await photoService.SavePhotoAsync(stream, photo.FileName, userId, taxi.Id);

        var oldPath = taxi.PlatePhotoPath;
        taxi.PlatePhotoUrl = url;
        taxi.PlatePhotoPath = filePath;
        taxi.UpdatedAt = DateTime.UtcNow;
        try
        {
            await unitOfWork.SaveChangesAsync();
        }
        catch (Exception ex) when (ex is DbUpdateConcurrencyException || PgRetry.IsRetryable(ex))
        {
            await PhotoCleanup.TryDeleteAsync(photoService, filePath, logger);
            return Concurrent();
        }
        catch
        {
            await PhotoCleanup.TryDeleteAsync(photoService, filePath, logger);
            throw;
        }

        if (oldPath != filePath) await PhotoCleanup.TryDeleteAsync(photoService, oldPath, logger);

        return CreatedResponse(new { photoUrl = url, status = taxi.Status }, url);
    }

    public const string SetDisplayPhotoSql = """
        WITH old AS (
            SELECT "Id", "DisplayPhotoPath", "DistrictId", "Status"
            FROM "TaxiListings"
            WHERE "Id" = @id AND "UserId" = @user AND NOT "IsDeleted"
            FOR UPDATE
        )
        UPDATE "TaxiListings" t
        SET "DisplayPhotoUrl" = @url, "DisplayPhotoPath" = @path, "UpdatedAt" = now()
        FROM old
        WHERE t."Id" = old."Id"
        RETURNING old."DisplayPhotoPath" AS "OldPath", old."DistrictId" AS "DistrictId", old."Status" AS "Status"
        """;

    private sealed class DisplayPhotoSwapRow
    {
        public string? OldPath { get; set; }
        public Guid? DistrictId { get; set; }
        public string Status { get; set; } = string.Empty;
    }

    // Display photo: shown to travellers, changeable in any status, no review, the taxi stays live.
    public static async Task<IResult> UploadDisplayPhoto(
        IFormFile photo, ClaimsPrincipal principal, ApplicationDbContext db, IPhotoService photoService, IServiceProvider sp)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var owned = await db.TaxiListings.AsNoTracking()
            .Where(t => t.UserId == userId && !t.IsDeleted)
            .Select(t => new { t.Id })
            .FirstOrDefaultAsync();
        if (owned == null) return NotFoundResponse(TaxiNotFound);
        var taxiId = owned.Id;
        if (PhotoUpload.Check(photo, PhotoNotImage, PhotoTooBig) is { } bad) return bad;

        using var stream = photo.OpenReadStream();
        var logger = PhotoCleanup.Logger(sp);
        var (url, filePath) = await photoService.SavePhotoAsync(stream, photo.FileName, userId, taxiId);

        DisplayPhotoSwapRow? swapped;
        try
        {
            swapped = (await db.Database.SqlQueryRaw<DisplayPhotoSwapRow>(SetDisplayPhotoSql,
                    new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = taxiId },
                    new NpgsqlParameter("user", NpgsqlDbType.Uuid) { Value = userId },
                    new NpgsqlParameter("url", NpgsqlDbType.Varchar) { Value = url },
                    new NpgsqlParameter("path", NpgsqlDbType.Varchar) { Value = filePath })
                .ToListAsync()).FirstOrDefault();
        }
        catch
        {
            await PhotoCleanup.TryDeleteAsync(photoService, filePath, logger);
            throw;
        }
        if (swapped == null)
        {
            await PhotoCleanup.TryDeleteAsync(photoService, filePath, logger);
            return NotFoundResponse(TaxiNotFound);
        }

        if (swapped.OldPath != filePath) await PhotoCleanup.TryDeleteAsync(photoService, swapped.OldPath, logger);
        if (swapped.Status == TaxiStatuses.Approved)
            await TaxiCache.InvalidateAsync(sp.GetService<IConnectionMultiplexer>(), swapped.DistrictId);

        return CreatedResponse(new { displayPhotoUrl = url }, url);
    }

    public static async Task<IResult> UpsertRoutes(
        UpsertTaxiRoutesRequest request,
        ClaimsPrincipal principal,
        IValidator<UpsertTaxiRoutesRequest> validator,
        IUnitOfWork unitOfWork,
        ApplicationDbContext db,
        ICityResolver resolver,
        ITripCityCatalog catalog,
        ILoggerFactory loggerFactory,
        IServiceProvider sp,
        IPlanEntitlementService entitlements)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            var error = validation.Errors[0];
            return error.ErrorCode is TaxiErrorTypes.TaxiRouteLimit or TaxiErrorTypes.TaxiRouteCityInvalid
                ? BadRequestResponse(error.ErrorMessage, error.ErrorCode)
                : BadRequestResponse(error.ErrorMessage);
        }

        var taxi = await unitOfWork.Taxis.GetLiveByUserAsync(userId);
        if (taxi == null) return NotFoundResponse(TaxiNotFound);
        if (taxi.Status == TaxiStatuses.PendingReview) return LockedResponse();
        if (!TaxiSetupRules.Step1Done(taxi)) return StepLocked(1);
        // Plan key Routes (contract 3.2, Q1): adding above the plan value is refused (409 TaxiRouteLimit); keeping or shrinking an
        // over-limit set passes (7 routes kept while payment is OFF). Hidden routes are never loaded, so the replace keeps them.
        var limits = await entitlements.GetLimitsAsync(userId, ListingKinds.Taxi);
        if (!PlanRowChoice.SaveAllowed(request.Routes.Count, limits.Routes, taxi.Routes.Count))
            return OwnerHttp.LimitReached(TaxiErrorTypes.TaxiRouteLimit, PlanFeatureKeys.Routes, taxi.Routes.Count, limits);
        // FB-47: a visible route may not copy a route the plan hides (it would come back as a duplicate on resubscribe).
        var hiddenKeys = (await db.TaxiRoutes.IgnoreQueryFilters().AsNoTracking()
                .Where(r => r.TaxiListingId == taxi.Id && r.HiddenByPlan)
                .Select(r => new { r.FromLatitude, r.FromLongitude, r.ToLatitude, r.ToLongitude, r.TripType })
                .ToListAsync())
            .Select(r => UpsertTaxiRoutesRequestValidator.Key(r.FromLatitude, r.FromLongitude, r.ToLatitude, r.ToLongitude, r.TripType))
            .ToHashSet();
        if (request.Routes.Any(r => hiddenKeys.Contains(UpsertTaxiRoutesRequestValidator.DuplicateKey(r))))
            return BadRequestResponse(UpsertTaxiRoutesRequestValidator.DuplicateMessage);
        // Resolved before any write, so a missing registration can never fail a route save after its commit.
        var pickupPlaces = sp.GetRequiredService<TaxiPickupPlaces>();
        var taxiRequests = sp.GetRequiredService<TaxiRequestClosure>();

        // Full replace by position: slot N keeps its row (and RouteIndex N), extra rows are removed,
        // so the (TaxiListingId, RouteIndex) unique index never sees a transient duplicate.
        var now = DateTime.UtcNow;
        var existing = taxi.Routes.OrderBy(r => r.RouteIndex).ToList();
        var picks = request.Routes.Select(TaxiRouteCityPicks.Of).ToList();
        var pickChecks = request.Routes
            .Select((input, i) => (input, picks[i], RouteEndsBefore.Of(existing.FirstOrDefault(r => r.RouteIndex == i + 1))))
            .ToList();
        if (await TaxiRouteCityKeys.CheckPicksAsync(catalog, pickChecks) is { } cityError)
            return ErrorResponse(cityError.Status, cityError.Message, cityError.Type);

        var saved = new List<(TaxiRoute Route, RouteEndsBefore? Before)>(request.Routes.Count);
        for (var i = 0; i < request.Routes.Count; i++)
        {
            var input = request.Routes[i];
            var index = (short)(i + 1);
            var route = existing.FirstOrDefault(r => r.RouteIndex == index);
            var before = RouteEndsBefore.Of(route);
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
            saved.Add((route, before));
        }
        foreach (var extra in existing.Where(r => r.RouteIndex > request.Routes.Count))
        {
            db.TaxiRoutes.Remove(extra);
            taxi.Routes.Remove(extra);
        }

        RecomputeMinFare(taxi);
        taxi.UpdatedAt = now;

        // Picked cities are stored as sent. Old builds: a city that cannot be trusted right now leaves the key null; the
        // route still saves and the backfill retries.
        var started = Stopwatch.GetTimestamp();
        var (_, routeKeys) = await TaxiRouteCityKeys.ResolveForSaveAsync(resolver, saved, picks, loggerFactory.CreateLogger("TaxiRouteCityKeys"), TaxiRouteCityKeys.RequestBudget);
        TaxiRouteCityKeys.MarkKeysWritten(db, saved.Select(s => s.Route));

        // O12: ringing and accepted offers no remaining route serves close in the same transaction, before the listing row
        // is written (lock order request -> driver listing). Selected offers and bookings stay. A null key keeps an offer
        // only while its lookup is still pending; the backfill checks those offers again once it answers.
        TaxiClosureResult taxiSignals;
        await unitOfWork.BeginTransactionAsync();
        try
        {
            taxiSignals = await taxiRequests.RoutesChangedAsync(db, taxi.Id, routeKeys);
            await unitOfWork.SaveChangesAsync();
            await unitOfWork.CommitTransactionAsync();
        }
        catch (Exception ex) when (ex is DbUpdateConcurrencyException || PgRetry.IsRetryable(ex))
        {
            await unitOfWork.RollbackTransactionAsync();
            return Concurrent();
        }
        // L15: two saves racing on a new (TaxiListingId, RouteIndex) row; the loser gets the same 409 as an xmin clash.
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            await unitOfWork.RollbackTransactionAsync();
            return Concurrent();
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }
        await taxiRequests.AfterCommitAsync(taxiSignals, poolChanged: false);
        // L6: a visible route removed on Free while some are hidden brings the newest hidden one back at once (as stay rooms do).
        if (hiddenKeys.Count > 0 && limits.PaymentEnabled && request.Routes.Count < limits.Routes
            && await RefillHiddenRoutesAsync(userId, unitOfWork, sp, entitlements))
        {
            db.ChangeTracker.Clear();
            taxi = await unitOfWork.Taxis.GetLiveByUserAsync(userId) ?? taxi;
        }
        await pickupPlaces.BumpAsync();

        if (taxi.Status == TaxiStatuses.Approved)
            await TaxiCache.InvalidateAsync(sp.GetService<IConnectionMultiplexer>(), taxi.DistrictId);
        // Resolve and names share one budget, so a cold city grid costs one wait, not two.
        var namesBudget = TaxiRouteCityKeys.RequestBudget - Stopwatch.GetElapsedTime(started);
        return OkResponse(await ToOwnerDtoAsync(taxi, unitOfWork, resolver, namesBudget > TimeSpan.Zero ? namesBudget : TimeSpan.Zero));
    }

    public static async Task<IResult> UpdateFare(
        Guid routeId,
        UpdateTaxiFareRequest request,
        ClaimsPrincipal principal,
        IValidator<UpdateTaxiFareRequest> validator,
        IUnitOfWork unitOfWork,
        ICityResolver resolver,
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
        if (route == null)
            return await HiddenRouteAsync(sp.GetRequiredService<ApplicationDbContext>(), taxi.Id, routeId)
                ? OwnerHttp.RowInactive()
                : NotFoundResponse("Route not found");

        var now = DateTime.UtcNow;
        route.Fare = request.Fare;
        route.UpdatedAt = now;
        RecomputeMinFare(taxi);
        taxi.UpdatedAt = now;

        try { await unitOfWork.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException) { return Concurrent(); }

        if (taxi.Status == TaxiStatuses.Approved)
            await TaxiCache.InvalidateAsync(sp.GetService<IConnectionMultiplexer>(), taxi.DistrictId);
        return OkResponse(await ToOwnerDtoAsync(taxi, unitOfWork, resolver));
    }

    // A route the plan hides (inactive after Plus ends): Delete is its one open action; visible routes go through the full replace
    // (PUT /me/routes). A hidden route rings nothing (its offers closed when it was hidden), so no closure runs and no visible row
    // moves. Lock order Users -> TaxiListings -> TaxiRoutes.
    public static async Task<IResult> DeleteHiddenRoute(
        Guid routeId, ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db, ILiveSlotService slots,
        PlanLimitsReconcile planRows, ICityResolver resolver, IServiceProvider sp)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var taxi = await unitOfWork.Taxis.GetLiveByUserAsync(userId);
        if (taxi == null) return NotFoundResponse(TaxiNotFound);
        if (taxi.Status == TaxiStatuses.PendingReview) return LockedResponse();
        if (taxi.Routes.Any(r => r.Id == routeId))
            return OwnerHttp.Refuse(OwnerErrorTypes.BadRequest, VisibleRouteDelete);

        IResult? refused = null;
        await unitOfWork.BeginTransactionAsync();
        try
        {
            await slots.LockOwnerAsync(userId);
            if (!await OwnerLocks.LockTaxiAsync(db, taxi.Id)) refused = NotFoundResponse(TaxiNotFound);
            else
            {
                var deleted = await db.Database.ExecuteSqlRawAsync(DeleteHiddenRouteSql,
                    new NpgsqlParameter("r", NpgsqlDbType.Uuid) { Value = routeId },
                    new NpgsqlParameter("t", NpgsqlDbType.Uuid) { Value = taxi.Id });
                if (deleted == 0)
                    refused = await db.TaxiRoutes.IgnoreQueryFilters().AsNoTracking().AnyAsync(r => r.Id == routeId && r.TaxiListingId == taxi.Id)
                        ? OwnerHttp.Refuse(OwnerErrorTypes.BadRequest, RouteActiveAgain)
                        : NotFoundResponse("Route not found");
                else
                {
                    var hidden =(await OwnerHiddenCounts.ReadAsync(db, userId, ListingKinds.Taxi, IstTime.TodayIst(DateTime.UtcNow))).Hidden;
                    if (hidden.Total == 0) await planRows.ClearLimitsNoticeAsync(userId);
                    await db.Database.ExecuteSqlRawAsync("""UPDATE "TaxiListings" SET "UpdatedAt" = now() WHERE "Id" = @t""",
                        new NpgsqlParameter("t", NpgsqlDbType.Uuid) { Value = taxi.Id });
                }
            }
            if (refused != null) await unitOfWork.RollbackTransactionAsync();
            else await unitOfWork.CommitTransactionAsync();
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }
        if (refused != null) return refused;

        db.ChangeTracker.Clear();
        taxi = await unitOfWork.Taxis.GetLiveByUserAsync(userId) ?? taxi;
        if (taxi.Status == TaxiStatuses.Approved)
            await TaxiCache.InvalidateAsync(sp.GetService<IConnectionMultiplexer>(), taxi.DistrictId);
        return OkResponse(await ToOwnerDtoAsync(taxi, unitOfWork, resolver));
    }

    // Own transaction after the route save: Users lock, then the plan rows (closure before the listing lock, contract 4.1).
    public static async Task<bool> RefillHiddenRoutesAsync(Guid userId, IUnitOfWork unitOfWork, IServiceProvider sp, IPlanEntitlementService entitlements)
    {
        var slots = sp.GetRequiredService<ILiveSlotService>();
        var planRows = sp.GetRequiredService<PlanLimitsReconcile>();
        PlanRowsEffect effect;
        await unitOfWork.BeginTransactionAsync();
        try
        {
            await slots.LockOwnerAsync(userId);
            var limits = await entitlements.GetLimitsAsync(userId, ListingKinds.Taxi);
            effect = await planRows.ApplyForOwnerAsync(userId, ListingKinds.Taxi, limits);
            if (effect.Changed && effect.HiddenAfter == 0) await planRows.ClearLimitsNoticeAsync(userId);
            await unitOfWork.CommitTransactionAsync();
        }
        catch (Exception ex)
        {
            // The save is committed; the daily pass repairs the rows.
            await unitOfWork.RollbackTransactionAsync();
            planRows.DropAfterCommit();
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("TaxiRoutes").LogWarning(ex, "taxi.routes.refill failed for {UserId}", userId);
            return false;
        }
        await planRows.AfterCommitAsync();
        return effect.Changed;
    }

    public const string VisibleRouteDelete = "Remove this route from Edit routes.";

    public const string RouteActiveAgain = "This route is active again.";

    public const string DeleteHiddenRouteSql = """
        DELETE FROM "TaxiRoutes" WHERE "Id" = @r AND "TaxiListingId" = @t AND "HiddenByPlan"
        """;

    private static Task<bool> HiddenRouteAsync(ApplicationDbContext db, Guid taxiId, Guid routeId)
        => db.TaxiRoutes.IgnoreQueryFilters().AsNoTracking().AnyAsync(r => r.Id == routeId && r.TaxiListingId == taxiId && r.HiddenByPlan);

    // A rate change never sends an Approved taxi back to review.
    public static async Task<IResult> UpdateLocalRate(
        UpdateTaxiLocalRateRequest request,
        ClaimsPrincipal principal,
        IValidator<UpdateTaxiLocalRateRequest> validator,
        IUnitOfWork unitOfWork,
        IServiceProvider sp,
        ITripEnquiryClosure tripClosure,
        ICityResolver resolver)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return BadRequestResponse(validation.Errors[0].ErrorMessage);
        if (TaxiRateRules.Error(request) is { } rateError) return BadRequestResponse(rateError);

        var taxi = await unitOfWork.Taxis.GetLiveByUserAsync(userId);
        if (taxi == null) return NotFoundResponse(TaxiNotFound);
        if (taxi.Status == TaxiStatuses.PendingReview) return LockedResponse();
        if (!TaxiSetupRules.Step1Done(taxi)) return StepLocked(1);

        var rateCleared = taxi.LocalDayRate != null && request.DayRate == null;
        taxi.LocalDayRate = request.DayRate;
        taxi.LocalDayKm = request.DayRate == null ? null : request.KmPerDay;
        taxi.UpdatedAt = DateTime.UtcNow;

        // A cleared rate ends the open trip leads (booked ones stay); pushes go out after commit.
        IReadOnlyList<TripClosurePush> tripPushes = [];
        await unitOfWork.BeginTransactionAsync();
        try
        {
            await unitOfWork.SaveChangesAsync();
            if (rateCleared) tripPushes = await tripClosure.OnTaxiRateClearedAsync(taxi.Id);
            await unitOfWork.CommitTransactionAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            await unitOfWork.RollbackTransactionAsync();
            return Concurrent();
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }
        if (rateCleared) await tripClosure.AfterCommitAsync(tripPushes);

        if (taxi.Status == TaxiStatuses.Approved)
            await TaxiCache.InvalidateAsync(sp.GetService<IConnectionMultiplexer>(), taxi.DistrictId);
        return OkResponse(await ToOwnerDtoAsync(taxi, unitOfWork, resolver));
    }

    public static async Task<IResult> Submit(
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db, IRabbitMqPublisher publisher, ICityResolver resolver)
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
        if (!TaxiRateRules.Done(taxi)) return BadRequestResponse(TaxiRateRules.SubmitMessage);

        var check = BusinessOnboardingRules.NeedsFlagCheckAtSubmit(taxi.EverApproved)
            ? await BusinessOnboardingGate.CheckAsync(db, taxi.DistrictId!.Value, taxi.Latitude!.Value, taxi.Longitude!.Value)
            : OnboardingCheck.Ok;
        if (BusinessOnboardingRules.IsOutsideDistrict(check))
            return BadRequestResponse("Pin your base inside the selected district", TaxiErrorTypes.TaxiBaseOutsideDistrict);
        if (check == OnboardingCheck.OnboardingClosed)
            return BadRequestResponse(BusinessOnboardingRules.OnboardingClosedMessage, BusinessOnboardingRules.OnboardingClosedType);

        var now = DateTime.UtcNow;
        SendBackToReview(taxi, now);
        taxi.UpdatedAt = now;

        try { await unitOfWork.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException) { return Concurrent(); }

        await PublishReviewRequestAsync(publisher, taxi.Id);

        return OkResponse(await ToOwnerDtoAsync(taxi, unitOfWork, resolver));
    }

    public static async Task<IResult> UpdateAvailability(
        UpdateTaxiAvailabilityRequest request,
        ClaimsPrincipal principal,
        IValidator<UpdateTaxiAvailabilityRequest> validator,
        IUnitOfWork unitOfWork,
        ApplicationDbContext db,
        ITripEnquiryClosure tripClosure,
        ICityResolver resolver,
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

        var wasAvailable = taxi.IsAvailable;
        taxi.IsAvailable = request.IsAvailable!.Value;
        taxi.UpdatedAt = DateTime.UtcNow;
        var paused = wasAvailable && !taxi.IsAvailable;
        var resumed = !wasAvailable && taxi.IsAvailable;
        var taxiRequests = sp.GetRequiredService<TaxiRequestClosure>();

        // A paused driver ends his unanswered local trip leads and his ringing taxi requests; pushes go out after commit.
        // The requests are closed before the listing row is written, keeping the lock order request -> driver listing.
        IReadOnlyList<TripClosurePush> tripPushes = [];
        var taxiSignals = resumed ? TaxiRequestClosure.DriverOnline(taxi.Id) : TaxiClosureResult.Empty;
        await unitOfWork.BeginTransactionAsync();
        try
        {
            if (paused) taxiSignals = await taxiRequests.DriverOfflineAsync(db, taxi.Id);
            await unitOfWork.SaveChangesAsync();
            if (paused) tripPushes = await TripLeadSilence.ClosePausedDriverLeadsAsync(db, unitOfWork.TripEnquiries, taxi.Id, taxi.UpdatedAt);
            await unitOfWork.CommitTransactionAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            await unitOfWork.RollbackTransactionAsync();
            return Concurrent();
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }
        if (paused) await tripClosure.AfterCommitAsync(tripPushes);
        await taxiRequests.AfterCommitAsync(taxiSignals, poolChanged: true);

        await TaxiCache.InvalidateAsync(sp.GetService<IConnectionMultiplexer>(), taxi.DistrictId);
        return OkResponse(await ToOwnerDtoAsync(taxi, unitOfWork, resolver));
    }

    // Soft delete (contract 6.2): the closure cancels active bookings; the owner tables go through the purge seam in the same
    // transaction. 200 { cancelledBookings } (old builds ignore the body).
    public static async Task<IResult> DeleteMine(
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, IListingPurgeService listingPurge, IServiceProvider sp,
        ITripEnquiryClosure tripClosure, ApplicationDbContext db, IOwnerListingPurge ownerPurge)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var taxi = await unitOfWork.Taxis.GetLiveByUserAsync(userId);
        if (taxi == null) return NotFoundResponse(TaxiNotFound);

        var removed = await ListingRemoval.SoftDeleteTaxiAsync(taxi, unitOfWork, db, tripClosure, ownerPurge, listingPurge, sp);
        return removed.Concurrent ? Concurrent() : OkResponse(new { cancelledBookings = removed.CancelledBookings });
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
}
