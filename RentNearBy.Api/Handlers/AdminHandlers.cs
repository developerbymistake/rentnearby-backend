using FluentValidation;
using Mapster;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Extensions;
using RentNearBy.Infrastructure.Services;
using static RentNearBy.Api.Extensions.ApiResults;
using System.Security.Claims;

namespace RentNearBy.Api.Handlers;

public static class AdminHandlers
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    public static async Task<IResult> GetDistricts(IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        if (!cache.TryGetValue("districts", out List<DistrictDto>? cached) || cached == null)
        {
            var districts = await unitOfWork.Districts.GetAllAsync();
            cached = districts.Select(d => d.Adapt<DistrictDto>()).ToList();
            cache.Set("districts", cached, CacheTtl);
        }
        return OkResponse(cached);
    }

    public static async Task<IResult> CreateDistrict(CreateDistrictRequest request, IValidator<CreateDistrictRequest> validator, IUnitOfWork unitOfWork, IGeocodingService geocoding, IMemoryCache cache)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        decimal lat, lng;
        if (request.Latitude.HasValue && request.Longitude.HasValue)
        {
            lat = request.Latitude.Value;
            lng = request.Longitude.Value;
        }
        else
        {
            var point = await geocoding.GeocodeAsync($"{request.Name.Trim()}, India");
            if (point is null)
                return BadRequestResponse($"Could not geocode '{request.Name}'. Provide coordinates manually.");
            lat = point.Latitude;
            lng = point.Longitude;
        }

        var district = new District { Id = Guid.NewGuid(), Name = request.Name.Trim(), Latitude = lat, Longitude = lng, CreatedAt = DateTime.UtcNow };
        await unitOfWork.Districts.AddAsync(district);
        await unitOfWork.SaveChangesAsync();

        cache.Remove("districts");

        return CreatedResponse(district.Adapt<DistrictDto>(), $"/api/v1/admin/districts/{district.Id}");
    }

    public static async Task<IResult> DeleteDistrict(Guid id, IUnitOfWork unitOfWork, IMemoryCache cache, ApplicationDbContext db)
    {
        var district = await unitOfWork.Districts.GetByIdAsync(id);
        if (district == null) return NotFoundResponse("District not found");

        if (await db.Listings.AnyAsync(l => l.DistrictId == id && l.IsActive))
            return BadRequestResponse("Cannot delete district with active listings");

        await unitOfWork.Districts.DeleteAsync(district);
        await unitOfWork.SaveChangesAsync();

        cache.Remove("districts");
        cache.Remove($"cities_{id}");
        cache.Remove("cities_all");

        return NoContentResponse();
    }

    public static async Task<IResult> GetCities(Guid? districtId, IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        var cacheKey = districtId.HasValue ? $"cities_{districtId}" : "cities_all";
        if (!cache.TryGetValue(cacheKey, out List<CityDto>? cached) || cached == null)
        {
            IEnumerable<City> cities = districtId.HasValue
                ? await unitOfWork.Cities.GetByDistrictIdAsync(districtId.Value)
                : await unitOfWork.Cities.GetAllAsync();
            cached = cities.Select(c => c.Adapt<CityDto>()).ToList();
            cache.Set(cacheKey, cached, CacheTtl);
        }
        return OkResponse(cached);
    }

    public static async Task<IResult> CreateCity(CreateCityRequest request, IValidator<CreateCityRequest> validator, IUnitOfWork unitOfWork, IGeocodingService geocoding, IMemoryCache cache)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var trimmedName = request.Name.Trim();
        var exists = await unitOfWork.Cities.GetByDistrictIdAsync(request.DistrictId);
        if (exists.Any(c => string.Equals(c.Name, trimmedName, StringComparison.OrdinalIgnoreCase)))
            return BadRequestResponse($"City '{trimmedName}' already exists in this district", "DuplicateCity");

        decimal lat, lng;
        if (request.Latitude.HasValue && request.Longitude.HasValue)
        {
            lat = request.Latitude.Value;
            lng = request.Longitude.Value;
        }
        else
        {
            var district = await unitOfWork.Districts.GetByIdAsync(request.DistrictId);
            if (district is null) return NotFoundResponse("District not found");

            var point = await geocoding.GeocodeAsync($"{trimmedName}, {district.Name}, India");
            if (point is null)
                return BadRequestResponse($"Could not geocode '{trimmedName}'. Provide coordinates manually.");
            lat = point.Latitude;
            lng = point.Longitude;
        }

        var city = new City { Id = Guid.NewGuid(), DistrictId = request.DistrictId, Name = trimmedName, Latitude = lat, Longitude = lng, CreatedAt = DateTime.UtcNow };
        await unitOfWork.Cities.AddAsync(city);
        await unitOfWork.SaveChangesAsync();

        cache.Remove($"cities_{request.DistrictId}");
        cache.Remove("cities_all");

        return CreatedResponse(city.Adapt<CityDto>(), $"/api/v1/admin/cities/{city.Id}");
    }

    public static async Task<IResult> Geocode(string q, IGeocodingService geocoding)
    {
        if (string.IsNullOrWhiteSpace(q))
            return BadRequestResponse("Query parameter 'q' is required");

        var point = await geocoding.GeocodeAsync(q.Trim());
        if (point is null)
            return NotFoundResponse("No results found for the given query");

        return OkResponse(new { latitude = point.Latitude, longitude = point.Longitude, displayName = point.DisplayName });
    }

    public static async Task<IResult> DeleteCity(Guid id, IUnitOfWork unitOfWork, IMemoryCache cache, ApplicationDbContext db)
    {
        var city = await unitOfWork.Cities.GetByIdAsync(id);
        if (city == null) return NotFoundResponse("City not found");

        if (await db.Listings.AnyAsync(l => l.CityId == id && l.IsActive))
            return BadRequestResponse("Cannot delete city with active listings");

        await unitOfWork.Cities.DeleteAsync(city);
        await unitOfWork.SaveChangesAsync();

        cache.Remove($"cities_{city.DistrictId}");
        cache.Remove("cities_all");

        return NoContentResponse();
    }

    public static async Task<IResult> GetRoomTypes(IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        if (!cache.TryGetValue("room_types", out List<RoomTypeDto>? cached) || cached == null)
        {
            var types = await unitOfWork.RoomTypes.GetAllAsync();
            cached = types.OrderBy(r => r.SortOrder).Select(r => r.Adapt<RoomTypeDto>()).ToList();
            cache.Set("room_types", cached, CacheTtl);
        }
        return OkResponse(cached);
    }

    public static async Task<IResult> CreateRoomType(CreateRoomTypeRequest request, IValidator<CreateRoomTypeRequest> validator, IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var roomType = new RoomType { Id = Guid.NewGuid(), Name = request.Name.Trim(), Description = request.Description, SortOrder = request.SortOrder, CreatedAt = DateTime.UtcNow };
        await unitOfWork.RoomTypes.AddAsync(roomType);
        await unitOfWork.SaveChangesAsync();

        cache.Remove("room_types");

        return CreatedResponse(roomType.Adapt<RoomTypeDto>(), $"/api/v1/admin/room-types/{roomType.Id}");
    }

    public static async Task<IResult> UpdateRoomType(Guid id, UpdateRoomTypeRequest request, IValidator<UpdateRoomTypeRequest> validator, IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var roomType = await unitOfWork.RoomTypes.GetByIdAsync(id);
        if (roomType == null) return NotFoundResponse("Room type not found");

        if (request.Name != null) roomType.Name = request.Name.Trim();
        if (request.Description != null) roomType.Description = request.Description;

        await unitOfWork.RoomTypes.UpdateAsync(roomType);
        await unitOfWork.SaveChangesAsync();

        cache.Remove("room_types");

        return OkResponse(roomType.Adapt<RoomTypeDto>());
    }

    public static async Task<IResult> DeleteRoomType(Guid id, IUnitOfWork unitOfWork, IMemoryCache cache, ApplicationDbContext db)
    {
        var roomType = await unitOfWork.RoomTypes.GetByIdAsync(id);
        if (roomType == null) return NotFoundResponse("Room type not found");

        if (await db.Listings.AnyAsync(l => l.RoomTypeId == id && l.IsActive))
            return BadRequestResponse("Cannot delete room type with active listings");

        await unitOfWork.RoomTypes.DeleteAsync(roomType);
        await unitOfWork.SaveChangesAsync();

        cache.Remove("room_types");

        return NoContentResponse();
    }

    public static async Task<IResult> GetStats(ApplicationDbContext db)
    {
        var totalUsers = await db.Users.CountAsync();
        var totalListings = await db.Listings.CountAsync();
        var activeListings = await db.Listings.CountAsync(l => l.IsActive);
        var listingsByDistrict = await db.Listings
            .Where(l => l.IsActive)
            .GroupBy(l => new { l.DistrictId, DistrictName = l.District.Name })
            .Select(g => new { District = g.Key.DistrictName, Count = g.Count() })
            .ToListAsync();

        var now = DateTime.UtcNow;
        var totalEarnings = await db.PaymentTransactions
            .Where(t => t.Status == "SUCCESS")
            .SumAsync(t => (int?)t.Amount) ?? 0;
        var currentMonthEarnings = await db.PaymentTransactions
            .Where(t => t.Status == "SUCCESS" && t.CompletedAt.HasValue &&
                        t.CompletedAt.Value.Year == now.Year &&
                        t.CompletedAt.Value.Month == now.Month)
            .SumAsync(t => (int?)t.Amount) ?? 0;

        return OkResponse(new AdminStatsDto
        {
            TotalUsers = totalUsers,
            TotalListings = totalListings,
            ActiveListings = activeListings,
            ListingsByDistrict = listingsByDistrict.ToDictionary(x => x.District, x => x.Count),
            TotalEarnings = totalEarnings,
            CurrentMonthEarnings = currentMonthEarnings,
        });
    }

    public static async Task<IResult> GetPaymentFeature(IUnitOfWork unitOfWork)
    {
        var paymentFeature = await unitOfWork.PaymentFeature.GetAsync();
        if (paymentFeature == null)
        {
            // Create default if doesn't exist
            paymentFeature = new PaymentFeature
            {
                Id = Guid.NewGuid(),
                IsEnabled = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            await unitOfWork.PaymentFeature.AddAsync(paymentFeature);
            await unitOfWork.SaveChangesAsync();
        }
        return OkResponse(paymentFeature);
    }

    public static async Task<IResult> UpdatePaymentFeature(PaymentFeatureUpdateRequest request, IUnitOfWork unitOfWork)
    {
        var paymentFeature = await unitOfWork.PaymentFeature.GetAsync();
        if (paymentFeature == null)
            return NotFoundResponse("Payment feature not configured");

        paymentFeature.IsEnabled = request.IsEnabled;
        paymentFeature.UpdatedAt = DateTime.UtcNow;

        await unitOfWork.PaymentFeature.UpdateAsync(paymentFeature);
        await unitOfWork.SaveChangesAsync();

        return OkResponse(paymentFeature);
    }

    public static async Task<IResult> GetUsers(
        ApplicationDbContext db,
        int page = 1,
        int pageSize = 20,
        bool? isActive = null,
        string? search = null,
        Guid? districtId = null,
        Guid? cityId = null)
    {
        pageSize = Math.Clamp(pageSize, 1, 100);
        page = Math.Max(1, page);

        var query = db.Users
            .Include(u => u.Listings)
            .Include(u => u.Memberships.OrderByDescending(m => m.CreatedAt).Take(1))
            .AsQueryable();

        if (isActive.HasValue)
            query = query.Where(u => u.IsActive == isActive.Value);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(u =>
                u.PhoneNumber.Contains(s) ||
                (u.Name != null && u.Name.ToLower().Contains(s)));
        }

        if (districtId.HasValue)
            query = query.Where(u => u.Listings.Any(l => l.DistrictId == districtId.Value && !l.IsDeleted));

        if (cityId.HasValue)
            query = query.Where(u => u.Listings.Any(l => l.CityId == cityId.Value && !l.IsDeleted));

        var result = await query
            .OrderByDescending(u => u.CreatedAt)
            .ToPagedResultAsync(page, pageSize, u =>
            {
                var nonDeleted = u.Listings.Where(l => !l.IsDeleted).ToList();
                var membership = u.Memberships
                    .OrderByDescending(m => m.CreatedAt)
                    .FirstOrDefault();

                return new AdminUserDto
                {
                    Id = u.Id,
                    PhoneNumber = u.PhoneNumber,
                    Name = u.Name,
                    IsAdmin = u.IsAdmin,
                    IsActive = u.IsActive,
                    HasUsedFreePlan = u.HasUsedFreePlan,
                    CreatedAt = u.CreatedAt,
                    TotalListings = nonDeleted.Count,
                    ActiveListings = nonDeleted.Count(l => l.IsActive),
                    CurrentMembership = membership == null ? null : new AdminMembershipDto
                    {
                        Id = membership.Id,
                        PlanType = membership.PlanType,
                        ValidFrom = membership.ValidFrom,
                        ValidUntil = membership.ValidUntil,
                        MaxRooms = membership.MaxRooms,
                        IsActive = membership.IsActive,
                    },
                };
            });

        return OkResponse(result);
    }

    public static async Task<IResult> UpdateUserStatus(
        Guid id,
        UpdateUserStatusRequest request,
        ApplicationDbContext db)
    {
        var user = await db.Users
            .Include(u => u.Listings.Where(l => !l.IsDeleted))
            .FirstOrDefaultAsync(u => u.Id == id);

        if (user == null) return NotFoundResponse("User not found");

        user.IsActive = request.IsActive;
        user.UpdatedAt = DateTime.UtcNow;

        foreach (var listing in user.Listings.Where(l => !l.IsDeleted))
        {
            listing.IsActive = request.IsActive;
            listing.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync();

        return OkResponse(new { id = user.Id, isActive = user.IsActive });
    }

    public static async Task<IResult> GetTransactions(
        ApplicationDbContext db,
        int page = 1,
        int pageSize = 20,
        string? status = null)
    {
        pageSize = Math.Clamp(pageSize, 1, 100);
        page = Math.Max(1, page);

        var query = db.PaymentTransactions
            .Include(t => t.User)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(status) && status.ToUpper() != "ALL")
            query = query.Where(t => t.Status == status.ToUpper());

        var result = await query
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new AdminTransactionDto
            {
                Id = t.Id,
                UserId = t.UserId,
                UserName = t.User.Name,
                UserPhone = t.User.PhoneNumber,
                PlanType = t.PlanType,
                Amount = t.Amount,
                Currency = t.Currency,
                Status = t.Status,
                RazorpayPaymentId = t.RazorpayPaymentId,
                FailureReason = t.FailureReason,
                CreatedAt = t.CreatedAt,
                CompletedAt = t.CompletedAt,
            })
            .ToPagedResultAsync(page, pageSize);

        return OkResponse(result);
    }

    public static async Task<IResult> ActivateMembership(
        Guid id,
        ActivateMembershipRequest request,
        ApplicationDbContext db)
    {
        var user = await db.Users.FindAsync(id);
        if (user == null) return NotFoundResponse("User not found");

        var planType = request.PlanType.Trim().ToUpperInvariant();
        var plan = await db.Plans.FirstOrDefaultAsync(p => p.PlanType == planType && p.IsEnabled);
        if (plan == null) return BadRequestResponse($"Plan '{planType}' not found or disabled.");

        bool isFree = plan.Price == 0;
        var now = DateTime.UtcNow;

        await db.UserMemberships
            .Where(m => m.UserId == id && m.IsActive)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.IsActive, false)
                .SetProperty(m => m.UpdatedAt, now));

        var membership = new UserMembership
        {
            Id = Guid.NewGuid(),
            UserId = id,
            PlanType = planType,
            ValidFrom = now,
            ValidUntil = now.AddDays(plan.Days),
            MaxRooms = plan.RoomLimit,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.UserMemberships.Add(membership);

        // Mark as used free plan for any price=0 plan
        if (isFree)
            user.HasUsedFreePlan = true;

        user.UpdatedAt = now;
        await db.SaveChangesAsync();

        return OkResponse(new AdminMembershipDto
        {
            Id = membership.Id,
            PlanType = membership.PlanType,
            ValidFrom = membership.ValidFrom,
            ValidUntil = membership.ValidUntil,
            MaxRooms = membership.MaxRooms,
            IsActive = membership.IsActive,
        });
    }

    public static async Task<IResult> GetPlans(ApplicationDbContext db)
    {
        var plans = await db.Plans
            .OrderBy(p => p.Price)
            .Select(p => new
            {
                id = p.Id,
                planType = p.PlanType,
                days = p.Days,
                price = p.Price,
                roomLimit = p.RoomLimit,
                isEnabled = p.IsEnabled,
            })
            .ToListAsync();

        return OkResponse(plans);
    }

    public static async Task<IResult> CreatePlan(
        CreatePlanRequest request,
        ApplicationDbContext db)
    {
        if (string.IsNullOrWhiteSpace(request.PlanType))
            return BadRequestResponse("Plan type name is required.");
        if (request.Price < 0)
            return BadRequestResponse("Price cannot be negative.");
        if (request.Days <= 0)
            return BadRequestResponse("Days must be greater than 0.");
        if (request.RoomLimit <= 0)
            return BadRequestResponse("Room limit must be greater than 0.");

        var key = request.PlanType.Trim().ToUpperInvariant();
        if (await db.Plans.AnyAsync(p => p.PlanType == key))
            return BadRequestResponse($"Plan '{key}' already exists.", "DuplicatePlan");

        var plan = new Plan
        {
            Id = Guid.NewGuid(),
            PlanType = key,
            Price = request.Price,
            Days = request.Days,
            RoomLimit = request.RoomLimit,
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        db.Plans.Add(plan);
        await db.SaveChangesAsync();

        return CreatedResponse(new
        {
            id = plan.Id,
            planType = plan.PlanType,
            days = plan.Days,
            price = plan.Price,
            roomLimit = plan.RoomLimit,
            isEnabled = plan.IsEnabled,
        }, $"/api/v1/admin/plans/{plan.Id}");
    }

    public static async Task<IResult> UpdatePlan(
        Guid id,
        UpdatePlanRequest request,
        ApplicationDbContext db)
    {
        var plan = await db.Plans.FindAsync(id);
        if (plan == null) return NotFoundResponse("Plan not found");

        if (request.Days.HasValue)
        {
            if (request.Days.Value <= 0) return BadRequestResponse("Days must be greater than 0");
            plan.Days = request.Days.Value;
        }
        if (request.Price.HasValue)
        {
            if (request.Price.Value < 0) return BadRequestResponse("Price cannot be negative");
            plan.Price = request.Price.Value;
        }
        if (request.RoomLimit.HasValue)
        {
            if (request.RoomLimit.Value <= 0) return BadRequestResponse("Room limit must be greater than 0");
            plan.RoomLimit = request.RoomLimit.Value;
        }
        if (request.IsEnabled.HasValue)
            plan.IsEnabled = request.IsEnabled.Value;

        plan.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        return OkResponse(new
        {
            id = plan.Id,
            planType = plan.PlanType,
            days = plan.Days,
            price = plan.Price,
            roomLimit = plan.RoomLimit,
            isEnabled = plan.IsEnabled,
        });
    }

    // ── Admin Listing endpoints ───────────────────────────────────────────────

    public record AdminToggleListingRequest(bool IsActive);

    public static async Task<IResult> GetAdminListings(
        ApplicationDbContext db,
        int page = 1,
        int pageSize = 20,
        Guid? districtId = null,
        Guid? cityId = null,
        Guid? roomTypeId = null,
        bool? isActive = null)
    {
        pageSize = Math.Clamp(pageSize, 1, 100);
        page = Math.Max(1, page);

        var query = db.Listings
            .Include(l => l.District)
            .Include(l => l.City)
            .Include(l => l.RoomType)
            .Include(l => l.User)
            .Include(l => l.Photos.OrderBy(p => p.PhotoOrder).Take(1))
            .Where(l => !l.IsDeleted);

        if (districtId.HasValue) query = query.Where(l => l.DistrictId == districtId.Value);
        if (cityId.HasValue) query = query.Where(l => l.CityId == cityId.Value);
        if (roomTypeId.HasValue) query = query.Where(l => l.RoomTypeId == roomTypeId.Value);
        if (isActive.HasValue) query = query.Where(l => l.IsActive == isActive.Value);

        var result = await query
            .OrderByDescending(l => l.CreatedAt)
            .ToPagedResultAsync(page, pageSize, l => new AdminListingDto
            {
                Id = l.Id,
                UserId = l.UserId,
                OwnerName = l.User?.Name,
                OwnerPhone = l.User?.PhoneNumber,
                DistrictName = l.District?.Name,
                CityName = l.City?.Name,
                RoomTypeName = l.RoomType?.Name,
                PriceMonthly = l.PriceMonthly,
                IsActive = l.IsActive,
                Address = l.Address,
                ThumbnailUrl = l.Photos.FirstOrDefault()?.PhotoUrl,
                PhotoCount = l.Photos.Count,
                CreatedAt = l.CreatedAt,
            });

        return OkResponse(result);
    }

    public static async Task<IResult> ToggleAdminListingStatus(
        Guid id, AdminToggleListingRequest request, ApplicationDbContext db)
    {
        var listing = await db.Listings.FindAsync(id);
        if (listing == null || listing.IsDeleted) return NotFoundResponse("Listing not found");

        listing.IsActive = request.IsActive;
        listing.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        return OkResponse(new { success = true, isActive = listing.IsActive });
    }

    public static async Task<IResult> DeleteAdminListing(
        Guid id, ApplicationDbContext db, IPhotoService photoService)
    {
        var listing = await db.Listings
            .Include(l => l.Photos)
            .FirstOrDefaultAsync(l => l.Id == id && !l.IsDeleted);
        if (listing == null) return NotFoundResponse("Listing not found");

        foreach (var photo in listing.Photos)
            await photoService.DeletePhotoAsync(photo.FilePath);

        listing.IsDeleted = true;
        listing.DeletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        return NoContentResponse();
    }
}
