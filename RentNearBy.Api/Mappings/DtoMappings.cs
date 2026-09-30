using System.Text.Json;
using Mapster;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;

namespace RentNearBy.Api.Mappings;

public static class DtoMappings
{
    public static void ConfigureMappings()
    {
        TypeAdapterConfig<User, UserDto>.NewConfig()
            .Map(dest => dest.Id, src => src.Id)
            .Map(dest => dest.PhoneNumber, src => src.PhoneNumber)
            .Map(dest => dest.Name, src => src.Name)
            .Map(dest => dest.IsActive, src => src.IsActive)
            .Map(dest => dest.IsPhoneVerified, src => src.IsPhoneVerified)
            .Map(dest => dest.HasUsedPhoneChange, src => src.HasUsedPhoneChange)
            .Map(dest => dest.IsContactVisible, src => src.IsContactVisible)
            .Map(dest => dest.CreatedAt, src => src.CreatedAt);

        TypeAdapterConfig<District, DistrictDto>.NewConfig();
        TypeAdapterConfig<City, CityDto>.NewConfig();
        TypeAdapterConfig<RoomType, RoomTypeDto>.NewConfig();
        TypeAdapterConfig<PlotType, PlotTypeDto>.NewConfig();

        TypeAdapterConfig<RoomListing, RoomListingDto>.NewConfig()
            .Map(dest => dest.DistrictName, src => src.District != null ? src.District.Name : null)
            .Map(dest => dest.CityName, src => src.City != null ? src.City.Name : null)
            .Map(dest => dest.RoomTypeName, src => src.RoomType != null ? src.RoomType.Name : null)
            .Map(dest => dest.OwnerName, src => src.User != null ? src.User.Name : null)
            .Map(dest => dest.OwnerPhone, src => src.User != null && src.User.IsContactVisible ? src.User.PhoneNumber : null)
            .Map(dest => dest.Photos, src => src.Photos != null
                ? src.Photos.OrderBy(p => p.PhotoOrder).Select(p => p.PhotoUrl).ToList()
                : new List<string>());

        TypeAdapterConfig<PlotListing, PlotListingDto>.NewConfig()
            .Map(dest => dest.PlotType, src => src.PlotType != null ? src.PlotType.Name : string.Empty)
            .Map(dest => dest.DistrictName, src => src.District != null ? src.District.Name : null)
            .Map(dest => dest.CityName, src => src.City != null ? src.City.Name : null)
            .Map(dest => dest.OwnerName, src => src.User != null ? src.User.Name : null)
            .Map(dest => dest.OwnerPhone, src => src.User != null && src.User.IsContactVisible ? src.User.PhoneNumber : null)
            .Map(dest => dest.Photos, src => src.Photos != null
                ? src.Photos.OrderBy(p => p.PhotoOrder).Select(p => p.PhotoUrl).ToList()
                : new List<string>());

        // Public (no-auth) by-slug DTOs — see PublicRoomListingDto/PublicPlotListingDto's doc comments
        // for exactly which fields are deliberately omitted.
        TypeAdapterConfig<RoomListing, PublicRoomListingDto>.NewConfig()
            .Map(dest => dest.DistrictName, src => src.District != null ? src.District.Name : null)
            .Map(dest => dest.CityName, src => src.City != null ? src.City.Name : null)
            .Map(dest => dest.RoomTypeName, src => src.RoomType != null ? src.RoomType.Name : null)
            .Map(dest => dest.Photos, src => src.Photos != null
                ? src.Photos.OrderBy(p => p.PhotoOrder).Select(p => p.PhotoUrl).ToList()
                : new List<string>());

        TypeAdapterConfig<PlotListing, PublicPlotListingDto>.NewConfig()
            .Map(dest => dest.PlotType, src => src.PlotType != null ? src.PlotType.Name : string.Empty)
            .Map(dest => dest.DistrictName, src => src.District != null ? src.District.Name : null)
            .Map(dest => dest.CityName, src => src.City != null ? src.City.Name : null)
            .Map(dest => dest.Photos, src => src.Photos != null
                ? src.Photos.OrderBy(p => p.PhotoOrder).Select(p => p.PhotoUrl).ToList()
                : new List<string>());

        // IsRead is deliberately NOT mapped here — it comes from a separate join
        // (NotificationListItem.IsRead), never a column on NotificationEvent itself. Handlers set it
        // explicitly after calling Adapt().
        TypeAdapterConfig<NotificationEvent, NotificationDto>.NewConfig()
            .Map(dest => dest.ActionArguments, src => src.ActionArgumentsJson == null
                ? null
                : JsonSerializer.Deserialize<Dictionary<string, string>>(src.ActionArgumentsJson, (JsonSerializerOptions?)null));
    }
}
