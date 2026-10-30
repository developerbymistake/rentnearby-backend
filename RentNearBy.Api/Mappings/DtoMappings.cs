using System.Globalization;
using System.Text.Json;
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
            .Map(dest => dest.IsActive, src => src.IsActive)
            .Map(dest => dest.IsPhoneVerified, src => src.IsPhoneVerified)
            .Map(dest => dest.HasUsedPhoneChange, src => src.HasUsedPhoneChange)
            .Map(dest => dest.RoleCorrectionUsed, src => src.RoleCorrectionUsed)
            .Map(dest => dest.IsContactVisible, src => src.IsContactVisible)
            .Map(dest => dest.CreatedAt, src => src.CreatedAt)
            .Map(dest => dest.Role, src => UserRoles.KeyOf(src.RoleId))
            .Map(dest => dest.RoleConfirmed, src => src.RoleConfirmedAt != null);

        TypeAdapterConfig<Role, RoleDto>.NewConfig();
        TypeAdapterConfig<RoleChangeRequest, RoleChangeRequestDto>.NewConfig()
            .Map(dest => dest.FromRole, src => UserRoles.KeyOf(src.FromRoleId))
            .Map(dest => dest.ToRole, src => UserRoles.KeyOf(src.ToRoleId))
            .Map(dest => dest.NextRequestAt, src => RoleChangeRules.NextRequestAllowedAt(src.Status, src.ReviewedAt, DateTime.UtcNow));

        TypeAdapterConfig<District, DistrictDto>.NewConfig();
        TypeAdapterConfig<City, CityDto>.NewConfig();
        TypeAdapterConfig<RoomType, RoomTypeDto>.NewConfig();
        TypeAdapterConfig<PlotType, PlotTypeDto>.NewConfig();
        TypeAdapterConfig<StayType, StayTypeDto>.NewConfig();
        // The licence is decrypted and the date formatted by AdminHandlers.GetDeletedAccounts.
        TypeAdapterConfig<DeletedAccountRecord, DeletedAccountRecordDto>.NewConfig()
            .Ignore(dest => dest.DrivingLicenceNumber)
            .Ignore(dest => dest.DateOfBirth);
        TypeAdapterConfig<StayRegistrationType, StayRegistrationTypeDto>.NewConfig()
            .Map(dest => dest.IsSeeded, src => StayRegistrationTypeSeed.IsSeeded(src.Id));

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

        // Stay: Address only on the owner DTO; PendingReportCount is set by the handler.
        TypeAdapterConfig<StayListing, StayOwnerDto>.NewConfig()
            .Map(dest => dest.Percent, src => StaySetupRules.Percent(src))
            .Map(dest => dest.Step1Done, src => StaySetupRules.Step1Done(src))
            .Map(dest => dest.RoomsDone, src => StaySetupRules.RoomsDone(src))
            .Map(dest => dest.RulesDone, src => StaySetupRules.RulesDone(src))
            .Map(dest => dest.Submitted, src => StaySetupRules.Submitted(src))
            .Map(dest => dest.Facilities, src => src.Facilities.ToList())
            .Map(dest => dest.StayTypeName, src => src.StayType != null ? src.StayType.Name : null)
            .Map(dest => dest.RegistrationTypeName, src => src.RegistrationTypeName)
            .Map(dest => dest.HasRegistrationNumber, src => src.RegistrationNumber != null)
            .Map(dest => dest.HasGstin, src => src.Gstin != null)
            .Map(dest => dest.DistrictName, src => src.District != null ? src.District.Name : null)
            .Map(dest => dest.CityName, src => src.City != null ? src.City.Name : null)
            .Map(dest => dest.Photos, src => src.Photos.OrderBy(p => p.PhotoOrder)
                .Select(p => new StayPhotoDto { Id = p.Id, Url = p.PhotoUrl }).ToList())
            .Map(dest => dest.Rooms, src => ToStayRoomDtos(src))
            .Map(dest => dest.Rules, src => ToStayRulesDto(src))
            .Ignore(dest => dest.PendingReportCount);

        TypeAdapterConfig<StayListing, StayPublicDto>.NewConfig()
            .Map(dest => dest.Facilities, src => src.Facilities.ToList())
            .Map(dest => dest.StayTypeName, src => src.StayType != null ? src.StayType.Name : null)
            .Map(dest => dest.DistrictName, src => src.District != null ? src.District.Name : null)
            .Map(dest => dest.CityName, src => src.City != null ? src.City.Name : null)
            .Map(dest => dest.CityId, src => src.City != null ? src.City.Id : (Guid?)null)
            .Map(dest => dest.CityLatitude, src => src.City != null ? src.City.Latitude : null)
            .Map(dest => dest.CityLongitude, src => src.City != null ? src.City.Longitude : null)
            .Map(dest => dest.Photos, src => src.Photos.OrderBy(p => p.PhotoOrder).Select(p => p.PhotoUrl).ToList())
            .Map(dest => dest.Rooms, src => ToStayRoomDtos(src))
            .Map(dest => dest.Rules, src => ToStayRulesDto(src))
            .Map(dest => dest.OwnerName, src => src.User != null ? src.User.Name : null)
            .Map(dest => dest.OwnerPhone, src => src.User != null && src.User.IsContactVisible ? src.User.PhoneNumber : null)
            .Ignore(dest => dest.HasReported);

        TypeAdapterConfig<StayListing, StayPublicSlugDto>.NewConfig()
            .Map(dest => dest.Slug, src => src.Slug ?? string.Empty)
            .Map(dest => dest.Facilities, src => src.Facilities.ToList())
            .Map(dest => dest.StayTypeName, src => src.StayType != null ? src.StayType.Name : null)
            .Map(dest => dest.DistrictName, src => src.District != null ? src.District.Name : null)
            .Map(dest => dest.CityName, src => src.City != null ? src.City.Name : null)
            .Map(dest => dest.CityId, src => src.City != null ? src.City.Id : (Guid?)null)
            .Map(dest => dest.CityLatitude, src => src.City != null ? src.City.Latitude : null)
            .Map(dest => dest.CityLongitude, src => src.City != null ? src.City.Longitude : null)
            .Map(dest => dest.Photos, src => src.Photos.OrderBy(p => p.PhotoOrder).Select(p => p.PhotoUrl).ToList())
            .Map(dest => dest.Rooms, src => ToStayRoomDtos(src))
            .Map(dest => dest.Rules, src => ToStayRulesDto(src));

        // Taxi: contacts are always on the owner DTO; GetById nulls them on the public DTO for anonymous callers.
        TypeAdapterConfig<TaxiVehicleType, TaxiVehicleTypeDto>.NewConfig()
            .Map(dest => dest.IsSeeded, src => TaxiVehicleTypeSeed.IsSeeded(src.Id));

        TypeAdapterConfig<TaxiListing, TaxiOwnerDto>.NewConfig()
            .Map(dest => dest.Percent, src => TaxiSetupRules.Percent(src))
            .Map(dest => dest.Step1Done, src => TaxiSetupRules.Step1Done(src))
            .Map(dest => dest.RoutesDone, src => TaxiSetupRules.RoutesDone(src))
            .Map(dest => dest.Submitted, src => TaxiSetupRules.Submitted(src))
            .Map(dest => dest.HasDrivingLicence, src => src.DrivingLicenceNumberEnc != null)
            .Map(dest => dest.HasDateOfBirth, src => src.DateOfBirth != default)
            .Map(dest => dest.VehicleTypeName, src => src.VehicleType != null ? src.VehicleType.Name : null)
            .Map(dest => dest.VehicleCategory, src => src.VehicleType != null ? src.VehicleType.Category : null)
            .Map(dest => dest.PlateDisplay, src => TaxiPlate.Format(src.NumberPlate))
            .Map(dest => dest.DistrictName, src => src.District != null ? src.District.Name : null)
            .Map(dest => dest.CityName, src => src.City != null ? src.City.Name : null)
            .Map(dest => dest.Routes, src => ToTaxiRouteDtos(src))
            .Ignore(dest => dest.PendingReportCount);

        TypeAdapterConfig<TaxiListing, TaxiPublicDto>.NewConfig()
            .Map(dest => dest.DriverName, src => src.User != null ? src.User.Name : null)
            .Map(dest => dest.VehicleTypeName, src => src.VehicleType != null ? src.VehicleType.Name : null)
            .Map(dest => dest.VehicleCategory, src => src.VehicleType != null ? src.VehicleType.Category : null)
            .Map(dest => dest.PlateDisplay, src => TaxiPlate.Format(src.NumberPlate))
            .Map(dest => dest.DistrictName, src => src.District != null ? src.District.Name : null)
            .Map(dest => dest.CityName, src => src.City != null ? src.City.Name : null)
            .Map(dest => dest.Routes, src => ToTaxiRouteDtos(src))
            .Ignore(dest => dest.HasReported);

        TypeAdapterConfig<TaxiListing, TaxiPublicSlugDto>.NewConfig()
            .Map(dest => dest.Slug, src => src.Slug ?? string.Empty)
            .Map(dest => dest.DriverName, src => src.User != null ? src.User.Name : null)
            .Map(dest => dest.VehicleTypeName, src => src.VehicleType != null ? src.VehicleType.Name : null)
            .Map(dest => dest.VehicleCategory, src => src.VehicleType != null ? src.VehicleType.Category : null)
            .Map(dest => dest.PlateDisplay, src => TaxiPlate.Format(src.NumberPlate))
            .Map(dest => dest.DistrictName, src => src.District != null ? src.District.Name : null)
            .Map(dest => dest.CityName, src => src.City != null ? src.City.Name : null)
            .Map(dest => dest.Routes, src => ToTaxiRouteDtos(src));

        // IsRead is deliberately NOT mapped here — it comes from a separate join
        // (NotificationListItem.IsRead), never a column on NotificationEvent itself. Handlers set it
        // explicitly after calling Adapt().
        TypeAdapterConfig<NotificationEvent, NotificationDto>.NewConfig()
            .Map(dest => dest.ActionArguments, src => src.ActionArgumentsJson == null
                ? null
                : JsonSerializer.Deserialize<Dictionary<string, string>>(src.ActionArgumentsJson, (JsonSerializerOptions?)null));
    }

    private static List<StayRoomDto> ToStayRoomDtos(StayListing s) =>
        s.Rooms.OrderBy(r => r.RoomIndex)
            .Select(r => new StayRoomDto
            {
                Id = r.Id,
                RoomIndex = r.RoomIndex,
                PricePerNight = r.PricePerNight,
                MaxGuests = r.MaxGuests,
                RoomName = r.RoomName,
                Meals = r.Meals,
            })
            .ToList();

    private static List<TaxiRouteDto> ToTaxiRouteDtos(TaxiListing t) =>
        t.Routes.OrderBy(r => r.RouteIndex)
            .Select(r => new TaxiRouteDto
            {
                Id = r.Id,
                RouteIndex = r.RouteIndex,
                FromName = r.FromName,
                FromLabel = r.FromLabel,
                FromLatitude = r.FromLatitude,
                FromLongitude = r.FromLongitude,
                ToName = r.ToName,
                ToLabel = r.ToLabel,
                ToLatitude = r.ToLatitude,
                ToLongitude = r.ToLongitude,
                Fare = r.Fare,
                TripType = r.TripType,
            })
            .ToList();

    // Null until the Rules step has been saved once.
    private static StayRulesDto? ToStayRulesDto(StayListing s) => s.RulesSavedAt == null
        ? null
        : new StayRulesDto
        {
            CheckInTime = s.CheckInTime?.ToString("HH:mm", CultureInfo.InvariantCulture),
            CheckOutTime = s.CheckOutTime?.ToString("HH:mm", CultureInfo.InvariantCulture),
            PetsAllowed = s.PetsAllowed,
            SmokingAllowed = s.SmokingAllowed,
            AlcoholAllowed = s.AlcoholAllowed,
            UnmarriedCouplesAllowed = s.UnmarriedCouplesAllowed,
            IdProofRequired = s.IdProofRequired,
            CancellationPolicy = s.CancellationPolicy,
        };
}
