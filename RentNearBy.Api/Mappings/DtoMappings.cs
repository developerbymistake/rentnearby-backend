using Mapster;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Models;

namespace RentNearBy.Api.Mappings;

public static class DtoMappings
{
    public static void ConfigureMappings()
    {
        TypeAdapterConfig<User, UserDto>.NewConfig()
            .Map(dest => dest.Id, src => src.Id)
            .Map(dest => dest.PhoneNumber, src => src.PhoneNumber)
            .Map(dest => dest.Name, src => src.Name)
            .Map(dest => dest.GmailId, src => src.GmailId)
            .Map(dest => dest.IsAdmin, src => src.IsAdmin)
            .Map(dest => dest.CreatedAt, src => src.CreatedAt);

        TypeAdapterConfig<City, CityDto>.NewConfig();
        TypeAdapterConfig<District, DistrictDto>.NewConfig();
        TypeAdapterConfig<RoomType, RoomTypeDto>.NewConfig();

        TypeAdapterConfig<Listing, ListingDto>.NewConfig()
            .Map(dest => dest.CityName, src => src.City != null ? src.City.Name : null)
            .Map(dest => dest.DistrictName, src => src.District != null ? src.District.Name : null)
            .Map(dest => dest.RoomTypeName, src => src.RoomType != null ? src.RoomType.Name : null)
            .Map(dest => dest.OwnerPhone, src => src.User != null ? src.User.PhoneNumber : null)
            .Map(dest => dest.Photos, src => src.Photos != null
                ? src.Photos.OrderBy(p => p.PhotoOrder).Select(p => p.PhotoUrl).ToList()
                : new List<string>());

        TypeAdapterConfig<NearbyListingResult, NearbyListingDto>.NewConfig()
            .Map(dest => dest.Id, src => src.Listing.Id)
            .Map(dest => dest.Title, src => src.Listing.Title)
            .Map(dest => dest.PriceMonthly, src => src.Listing.PriceMonthly)
            .Map(dest => dest.Latitude, src => src.Listing.Latitude)
            .Map(dest => dest.Longitude, src => src.Listing.Longitude)
            .Map(dest => dest.RoomTypeName, src => src.Listing.RoomType != null ? src.Listing.RoomType.Name : null)
            .Map(dest => dest.OwnerPhone, src => src.Listing.User != null ? src.Listing.User.PhoneNumber : null)
            .Map(dest => dest.ThumbnailUrl, src => src.Listing.Photos != null && src.Listing.Photos.Any()
                ? src.Listing.Photos.OrderBy(p => p.PhotoOrder).First().PhotoUrl
                : null)
            .Map(dest => dest.DistanceKm, src => Math.Round(src.DistanceKm, 2));
    }
}
