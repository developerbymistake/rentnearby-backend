using FluentValidation;
using Mapster;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

public static class AdminHandlers
{
    public static async Task<IResult> GetDistricts(IUnitOfWork unitOfWork)
    {
        var districts = await unitOfWork.Districts.GetAllAsync();
        return OkResponse(districts.Select(d => d.Adapt<DistrictDto>()).ToList());
    }

    public static async Task<IResult> CreateDistrict(CreateDistrictRequest request, IValidator<CreateDistrictRequest> validator, IUnitOfWork unitOfWork)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var district = new District { Id = Guid.NewGuid(), Name = request.Name.Trim(), Latitude = request.Latitude, Longitude = request.Longitude, CreatedAt = DateTime.UtcNow };
        await unitOfWork.Districts.AddAsync(district);
        await unitOfWork.SaveChangesAsync();

        return CreatedResponse(district.Adapt<DistrictDto>(), $"/api/v1/admin/districts/{district.Id}");
    }

    public static async Task<IResult> DeleteDistrict(Guid id, IUnitOfWork unitOfWork)
    {
        var district = await unitOfWork.Districts.GetByIdAsync(id);
        if (district == null) return NotFoundResponse("District not found");

        await unitOfWork.Districts.DeleteAsync(district);
        await unitOfWork.SaveChangesAsync();
        return NoContentResponse();
    }

    public static async Task<IResult> GetCities(Guid? districtId, IUnitOfWork unitOfWork)
    {
        IEnumerable<City> cities = districtId.HasValue
            ? await unitOfWork.Cities.GetByDistrictIdAsync(districtId.Value)
            : await unitOfWork.Cities.GetAllAsync();

        return OkResponse(cities.Select(c => c.Adapt<CityDto>()).ToList());
    }

    public static async Task<IResult> CreateCity(CreateCityRequest request, IValidator<CreateCityRequest> validator, IUnitOfWork unitOfWork)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var city = new City { Id = Guid.NewGuid(), DistrictId = request.DistrictId, Name = request.Name.Trim(), Latitude = request.Latitude, Longitude = request.Longitude, CreatedAt = DateTime.UtcNow };
        await unitOfWork.Cities.AddAsync(city);
        await unitOfWork.SaveChangesAsync();

        return CreatedResponse(city.Adapt<CityDto>(), $"/api/v1/admin/cities/{city.Id}");
    }

    public static async Task<IResult> DeleteCity(Guid id, IUnitOfWork unitOfWork)
    {
        var city = await unitOfWork.Cities.GetByIdAsync(id);
        if (city == null) return NotFoundResponse("City not found");

        await unitOfWork.Cities.DeleteAsync(city);
        await unitOfWork.SaveChangesAsync();
        return NoContentResponse();
    }

    public static async Task<IResult> GetRoomTypes(IUnitOfWork unitOfWork)
    {
        var types = await unitOfWork.RoomTypes.GetAllAsync();
        return OkResponse(types.Select(r => r.Adapt<RoomTypeDto>()).ToList());
    }

    public static async Task<IResult> CreateRoomType(CreateRoomTypeRequest request, IValidator<CreateRoomTypeRequest> validator, IUnitOfWork unitOfWork)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var roomType = new RoomType { Id = Guid.NewGuid(), Name = request.Name.Trim(), Description = request.Description, CreatedAt = DateTime.UtcNow };
        await unitOfWork.RoomTypes.AddAsync(roomType);
        await unitOfWork.SaveChangesAsync();

        return CreatedResponse(roomType.Adapt<RoomTypeDto>(), $"/api/v1/admin/room-types/{roomType.Id}");
    }

    public static async Task<IResult> DeleteRoomType(Guid id, IUnitOfWork unitOfWork)
    {
        var roomType = await unitOfWork.RoomTypes.GetByIdAsync(id);
        if (roomType == null) return NotFoundResponse("Room type not found");

        await unitOfWork.RoomTypes.DeleteAsync(roomType);
        await unitOfWork.SaveChangesAsync();
        return NoContentResponse();
    }

    public static async Task<IResult> GetStats(ApplicationDbContext db)
    {
        var totalUsers = await db.Users.CountAsync();
        var totalListings = await db.Listings.CountAsync();
        var activeListings = await db.Listings.CountAsync(l => l.IsActive);
        var listingsByDistrict = await db.Listings
            .Where(l => l.IsActive)
            .GroupBy(l => l.District.Name)
            .Select(g => new { District = g.Key, Count = g.Count() })
            .ToListAsync();

        return OkResponse(new AdminStatsDto
        {
            TotalUsers = totalUsers,
            TotalListings = totalListings,
            ActiveListings = activeListings,
            ListingsByDistrict = listingsByDistrict.ToDictionary(x => x.District, x => x.Count)
        });
    }
}
