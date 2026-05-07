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
    public static async Task<IResult> GetNearby(
        double latitude, double longitude, double radius, Guid districtId,
        IUnitOfWork unitOfWork,
        int page = 1, int pageSize = 30)
    {
        if (radius <= 0 || radius > 50)
            return BadRequestResponse("Radius must be between 1 and 50 km");
        if (pageSize < 1 || pageSize > 100) pageSize = 30;
        if (page < 1) page = 1;

        var allResults = (await unitOfWork.Listings.GetNearbyAsync(latitude, longitude, radius, districtId)).ToList();
        var paged = allResults
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(r => r.Adapt<NearbyListingDto>())
            .ToList();

        return Results.Ok(new { status = "success", data = paged, hasMore = allResults.Count > page * pageSize });
    }

    public static async Task<IResult> Search(Guid? districtId, Guid? roomTypeId, int? priceMin, int? priceMax, IUnitOfWork unitOfWork)
    {
        var listings = await unitOfWork.Listings.SearchAsync(districtId, roomTypeId, priceMin, priceMax);
        return OkResponse(listings.Select(l => l.Adapt<ListingDto>()).ToList());
    }

    public static async Task<IResult> GetById(Guid id, IUnitOfWork unitOfWork)
    {
        var listing = await unitOfWork.Listings.GetByIdWithPhotosAsync(id);
        if (listing == null) return NotFoundResponse("Listing not found");
        return OkResponse(listing.Adapt<ListingDto>());
    }

    public static async Task<IResult> GetMyListings(ClaimsPrincipal principal, IUnitOfWork unitOfWork)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var listings = await unitOfWork.Listings.GetByUserIdAsync(userId);
        return OkResponse(listings.Select(l => l.Adapt<ListingDto>()).ToList());
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

        var listing = new Listing
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            RoomTypeId = request.RoomTypeId,
            Title = request.Title,
            Description = request.Description,
            PriceMonthly = request.PriceMonthly,
            PricePerDay = request.PricePerDay,
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
        if (request.Title != null) listing.Title = request.Title;
        if (request.Description != null) listing.Description = request.Description;
        if (request.PriceMonthly.HasValue) listing.PriceMonthly = request.PriceMonthly.Value;
        if (request.PricePerDay.HasValue) listing.PricePerDay = request.PricePerDay.Value;
        if (request.Latitude.HasValue) listing.Latitude = request.Latitude.Value;
        if (request.Longitude.HasValue) listing.Longitude = request.Longitude.Value;
        if (request.Address != null) listing.Address = request.Address;
        if (request.CityId.HasValue) listing.CityId = request.CityId.Value;
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

        listing.Photos.Add(listingPhoto);
        await unitOfWork.SaveChangesAsync();

        return CreatedResponse(new { photoUrl = url, photoId = listingPhoto.Id }, url);
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
        listing.Photos.Remove(photo);
        await unitOfWork.SaveChangesAsync();

        return NoContentResponse();
    }
}
