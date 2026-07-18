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

        // ── Local Services Marketplace / Expert Consultations ──────────────────

        TypeAdapterConfig<ServiceSection, ServiceSectionDto>.NewConfig();
        TypeAdapterConfig<ServiceCategory, ServiceCategoryDto>.NewConfig();
        TypeAdapterConfig<Service, ServiceListItemDto>.NewConfig();
        TypeAdapterConfig<ServicePackage, ServicePackagePreviewDto>.NewConfig();
        TypeAdapterConfig<Inclusion, InclusionDto>.NewConfig();

        TypeAdapterConfig<Service, ServiceDetailDto>.NewConfig()
            .Map(dest => dest.Packages, src => src.Packages.OrderBy(p => p.SortOrder))
            .Map(dest => dest.ServiceSectionId, src => src.ServiceCategory.ServiceSectionId)
            .Map(dest => dest.ServiceSectionName, src => src.ServiceCategory.ServiceSection.Name);

        TypeAdapterConfig<ServicePackage, ServicePackageDto>.NewConfig()
            .Map(dest => dest.Inclusions, src => src.PackageInclusions
                .OrderBy(pi => pi.Inclusion.SortOrder)
                .Select(pi => pi.Inclusion));

        // ServiceCategoryIds/Names flattened from the AgentServiceCategory join.
        TypeAdapterConfig<Agent, AgentDto>.NewConfig()
            .Map(dest => dest.ServiceCategoryIds, src => src.AgentServiceCategories.Select(ac => ac.ServiceCategoryId))
            .Map(dest => dest.ServiceCategoryNames, src => src.AgentServiceCategories.Select(ac => ac.ServiceCategory.Name));

        TypeAdapterConfig<InquiryStatusHistory, InquiryStatusHistoryDto>.NewConfig()
            .Map(dest => dest.ChangedByAdminName, src => src.ChangedByAdmin != null ? src.ChangedByAdmin.Name : null);

        // ServiceSectionName resolved through Service -> ServiceCategory -> ServiceSection.
        TypeAdapterConfig<Inquiry, InquiryListItemDto>.NewConfig()
            .Map(dest => dest.ServiceName, src => src.Service.Name)
            .Map(dest => dest.ServiceSectionId, src => src.Service.ServiceCategory.ServiceSectionId)
            .Map(dest => dest.ServiceSectionName, src => src.Service.ServiceCategory.ServiceSection.Name)
            .Map(dest => dest.ServicePackageName, src => src.ServicePackage.Name)
            .Map(dest => dest.AssignedAgentName, src => src.AssignedAgent != null ? src.AssignedAgent.Name : null);

        TypeAdapterConfig<Inquiry, InquiryDetailDto>.NewConfig()
            .Map(dest => dest.ServiceName, src => src.Service.Name)
            .Map(dest => dest.ServiceSectionId, src => src.Service.ServiceCategory.ServiceSectionId)
            .Map(dest => dest.ServiceSectionName, src => src.Service.ServiceCategory.ServiceSection.Name)
            .Map(dest => dest.ServicePackageName, src => src.ServicePackage.Name)
            .Map(dest => dest.AssignedAgent, src => src.AssignedAgent);
    }
}
