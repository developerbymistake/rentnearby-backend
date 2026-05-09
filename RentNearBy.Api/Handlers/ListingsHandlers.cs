using System.Security.Claims;
using FluentValidation;
using Mapster;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Services;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

public static class ListingsHandlers
{
    public static async Task<IResult> GetContext(double lat, double lng, IUnitOfWork unitOfWork)
    {
        var districts = (await unitOfWork.Districts.GetAllAsync()).ToList();
        if (districts.Count == 0) return BadRequestResponse("No districts configured");

        var nearestDistrict = districts
            .Select(d => new { District = d, Dist = Haversine(lat, lng, (double)(d.Latitude ?? 0), (double)(d.Longitude ?? 0)) })
            .MinBy(x => x.Dist)!.District;

        var cities = (await unitOfWork.Cities.GetByDistrictIdAsync(nearestDistrict.Id)).ToList();

        var nearestCity = cities
            .Where(c => c.Latitude.HasValue && c.Longitude.HasValue)
            .Select(c => new { City = c, Dist = Haversine(lat, lng, (double)c.Latitude!, (double)c.Longitude!) })
            .MinBy(x => x.Dist)?.City ?? cities.FirstOrDefault();

        return OkResponse(new
        {
            district = new { id = nearestDistrict.Id, name = nearestDistrict.Name, latitude = nearestDistrict.Latitude, longitude = nearestDistrict.Longitude },
            nearestCityId = nearestCity?.Id,
            cities = cities.Select(c => new { id = c.Id, districtId = c.DistrictId, name = c.Name, latitude = c.Latitude, longitude = c.Longitude }).ToList(),
        });
    }

    private static double Haversine(double lat1, double lng1, double lat2, double lng2)
    {
        const double R = 6371.0;
        var dlat = (lat2 - lat1) * Math.PI / 180;
        var dlng = (lng2 - lng1) * Math.PI / 180;
        var a = Math.Sin(dlat / 2) * Math.Sin(dlat / 2) +
                Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) *
                Math.Sin(dlng / 2) * Math.Sin(dlng / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    public static async Task<IResult> GetNearby(
        double latitude, double longitude, double radius, Guid cityId,
        IUnitOfWork unitOfWork,
        ClaimsPrincipal principal)
    {
        if (radius <= 0 || radius > 50)
            return BadRequestResponse("Radius must be between 1 and 50 km");

        var results = await unitOfWork.Listings.GetNearbyAsync(latitude, longitude, radius, cityId);
        var isAuthenticated = principal.Identity?.IsAuthenticated == true;

        var items = results.Select(r =>
        {
            var dto = r.Adapt<NearbyListingDto>();
            if (!isAuthenticated) dto.OwnerPhone = null;
            return dto;
        }).ToList();

        return OkResponse(new { items });
    }

    public static async Task<IResult> Search(Guid? districtId, Guid? roomTypeId, int? priceMin, int? priceMax, IUnitOfWork unitOfWork)
    {
        var listings = await unitOfWork.Listings.SearchAsync(districtId, roomTypeId, priceMin, priceMax);
        return OkResponse(listings.Select(l => l.Adapt<ListingDto>()).ToList());
    }

    public static async Task<IResult> GetById(Guid id, IUnitOfWork unitOfWork, ClaimsPrincipal principal)
    {
        var listing = await unitOfWork.Listings.GetByIdWithPhotosAsync(id);
        if (listing == null) return NotFoundResponse("Listing not found");
        var dto = listing.Adapt<ListingDto>();
        if (principal.Identity?.IsAuthenticated != true) dto.OwnerPhone = null;
        return OkResponse(dto);
    }

    public static async Task<IResult> GetMyListings(
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, int page = 1, int pageSize = 10)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();
        if (pageSize < 1 || pageSize > 50) pageSize = 10;
        if (page < 1) page = 1;

        var (items, hasMore) = await unitOfWork.Listings.GetByUserIdPagedAsync(userId, page, pageSize);
        return OkResponse(new { items = items.Select(l => l.Adapt<ListingDto>()).ToList(), hasMore });
    }

    public static async Task<IResult> CreateListing(
        CreateListingRequest request,
        ClaimsPrincipal principal,
        IValidator<CreateListingRequest> validator,
        IUnitOfWork unitOfWork)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return BadRequestResponse(validation.Errors[0].ErrorMessage);

        if (request.CityId.HasValue)
        {
            var city = await unitOfWork.Cities.GetByIdAsync(request.CityId.Value);
            if (city == null) return BadRequestResponse("Selected city does not exist");
            if (city.DistrictId != request.DistrictId)
                return BadRequestResponse("Selected city does not belong to the selected district");
        }

        var listing = new Listing
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
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await unitOfWork.Listings.AddAsync(listing);
        await unitOfWork.SaveChangesAsync();

        return CreatedResponse(new { listingId = listing.Id }, $"/api/v1/listings/{listing.Id}");
    }

    public static async Task<IResult> UpdateListing(Guid id, UpdateListingRequest request, ClaimsPrincipal principal, IUnitOfWork unitOfWork)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var listing = await unitOfWork.Listings.GetByIdAsync(id);
        if (listing == null) return NotFoundResponse("Listing not found");
        if (listing.UserId != userId) return ForbiddenResponse("You do not own this listing");

        if (request.RoomTypeId.HasValue) listing.RoomTypeId = request.RoomTypeId.Value;
        if (request.Description != null) listing.Description = request.Description;
        if (request.PriceMonthly.HasValue) listing.PriceMonthly = request.PriceMonthly.Value;
        if (request.Latitude.HasValue) listing.Latitude = request.Latitude.Value;
        if (request.Longitude.HasValue) listing.Longitude = request.Longitude.Value;
        if (request.Address != null) listing.Address = request.Address;
        if (request.CityId.HasValue)
        {
            listing.CityId = request.CityId.Value;
            var city = await unitOfWork.Cities.GetByIdAsync(request.CityId.Value);
            if (city != null) listing.DistrictId = city.DistrictId;
        }
        if (request.IsActive.HasValue) listing.IsActive = request.IsActive.Value;
        listing.UpdatedAt = DateTime.UtcNow;

        await unitOfWork.Listings.UpdateAsync(listing);
        await unitOfWork.SaveChangesAsync();

        return OkResponse(new { success = true });
    }

    public static async Task<IResult> DeleteListing(Guid id, ClaimsPrincipal principal, IUnitOfWork unitOfWork, IPhotoService photoService)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var listing = await unitOfWork.Listings.GetByIdAsync(id);
        if (listing == null) return NotFoundResponse("Listing not found");
        if (listing.UserId != userId) return ForbiddenResponse("You do not own this listing");

        await photoService.DeleteListingPhotosAsync(userId, id);
        await unitOfWork.Listings.DeleteAsync(listing);
        await unitOfWork.SaveChangesAsync();

        return NoContentResponse();
    }

    public static async Task<IResult> UploadPhoto(Guid id, IFormFile photo, ClaimsPrincipal principal, IUnitOfWork unitOfWork, IPhotoService photoService)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var listing = await unitOfWork.Listings.GetByIdWithPhotosAsync(id);
        if (listing == null) return NotFoundResponse("Listing not found");
        if (listing.UserId != userId) return ForbiddenResponse("You do not own this listing");
        if (listing.Photos.Count >= 5) return BadRequestResponse("Maximum 5 photos allowed per listing");
        if (photo.Length > 10 * 1024 * 1024) return BadRequestResponse("Photo size must not exceed 10MB");

        using var stream = photo.OpenReadStream();
        if (!IsValidImageMagicBytes(stream)) return BadRequestResponse("File must be a valid image (JPEG, PNG or WebP)");
        stream.Position = 0;
        var (url, filePath) = await photoService.SavePhotoAsync(stream, photo.FileName, userId, id);

        var listingPhoto = new ListingPhoto
        {
            Id = Guid.NewGuid(),
            ListingId = id,
            PhotoUrl = url,
            FilePath = filePath,
            PhotoOrder = listing.Photos.Count,
            UploadedAt = DateTime.UtcNow
        };

        await unitOfWork.Listings.AddPhotoAsync(listingPhoto);
        await unitOfWork.SaveChangesAsync();

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

    public static async Task<IResult> DeletePhoto(Guid id, Guid photoId, ClaimsPrincipal principal, IUnitOfWork unitOfWork, IPhotoService photoService)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var listing = await unitOfWork.Listings.GetByIdWithPhotosAsync(id);
        if (listing == null) return NotFoundResponse("Listing not found");
        if (listing.UserId != userId) return ForbiddenResponse("You do not own this listing");

        var photo = listing.Photos.FirstOrDefault(p => p.Id == photoId);
        if (photo == null) return NotFoundResponse("Photo not found");

        await photoService.DeletePhotoAsync(photo.FilePath);
        unitOfWork.Listings.RemovePhoto(photo);
        await unitOfWork.SaveChangesAsync();

        return NoContentResponse();
    }
}
