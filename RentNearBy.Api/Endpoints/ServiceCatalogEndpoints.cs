using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

public static class ServiceCatalogEndpoints
{
    // Consumer-facing reads, mounted at "/api/v1/services". Same handlers as the admin GETs below —
    // no active-only filtering server-side, mirrors GetDistricts/GetCities being dual-mounted under
    // both /admin/districts and /listings/locations/districts.
    public static RouteGroupBuilder MapServiceCatalogEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/sections", ServiceCatalogHandlers.GetServiceSections);
        group.MapGet("/sections/{id:guid}", ServiceCatalogHandlers.GetServiceSectionById);

        group.MapGet("/categories", ServiceCatalogHandlers.GetServiceCategories);
        group.MapGet("/categories/{id:guid}", ServiceCatalogHandlers.GetServiceCategoryById);

        group.MapGet("", ServiceCatalogHandlers.GetServices);
        group.MapGet("/preview", ServiceCatalogHandlers.GetServicesPreview);
        group.MapGet("/{id:guid}", ServiceCatalogHandlers.GetServiceById);

        group.MapGet("/packages", ServiceCatalogHandlers.GetServicePackages);
        group.MapGet("/packages/{id:guid}", ServiceCatalogHandlers.GetServicePackageById);

        group.MapGet("/inclusions", ServiceCatalogHandlers.GetInclusions);
        group.MapGet("/inclusions/{id:guid}", ServiceCatalogHandlers.GetInclusionById);

        return group;
    }

    // Admin CRUD, mounted at "/api/v1/admin". Flat sibling routes + optional query-param parent
    // filter, exact convention of GET /admin/cities?districtId=.
    public static RouteGroupBuilder MapAdminServiceCatalogEndpoints(this RouteGroupBuilder group)
    {
        // Service Sections
        group.MapGet("/service-sections", ServiceCatalogHandlers.GetServiceSections);
        group.MapGet("/service-sections/{id:guid}", ServiceCatalogHandlers.GetServiceSectionById);
        group.MapPost("/service-sections", ServiceCatalogHandlers.AdminCreateServiceSection).RequireAuthorization("AdminOnly");
        group.MapPut("/service-sections/{id:guid}", ServiceCatalogHandlers.AdminUpdateServiceSection).RequireAuthorization("AdminOnly");
        group.MapDelete("/service-sections/{id:guid}", ServiceCatalogHandlers.AdminDeleteServiceSection).RequireAuthorization("AdminOnly");

        // Service Categories
        group.MapGet("/service-categories", ServiceCatalogHandlers.GetServiceCategories);
        group.MapGet("/service-categories/{id:guid}", ServiceCatalogHandlers.GetServiceCategoryById);
        group.MapPost("/service-categories", ServiceCatalogHandlers.AdminCreateServiceCategory).RequireAuthorization("AdminOnly");
        group.MapPut("/service-categories/{id:guid}", ServiceCatalogHandlers.AdminUpdateServiceCategory).RequireAuthorization("AdminOnly");
        group.MapDelete("/service-categories/{id:guid}", ServiceCatalogHandlers.AdminDeleteServiceCategory).RequireAuthorization("AdminOnly");

        // Services (+ cover photo)
        group.MapGet("/services", ServiceCatalogHandlers.GetServices);
        group.MapGet("/services/{id:guid}", ServiceCatalogHandlers.GetServiceById);
        group.MapPost("/services", ServiceCatalogHandlers.AdminCreateService).RequireAuthorization("AdminOnly");
        group.MapPut("/services/{id:guid}", ServiceCatalogHandlers.AdminUpdateService).RequireAuthorization("AdminOnly");
        group.MapDelete("/services/{id:guid}", ServiceCatalogHandlers.AdminDeleteService).RequireAuthorization("AdminOnly");
        group.MapPost("/services/{id:guid}/cover-photo", ServiceCatalogHandlers.AdminUploadServiceCoverPhoto).RequireAuthorization("AdminOnly").DisableAntiforgery();
        group.MapDelete("/services/{id:guid}/cover-photo", ServiceCatalogHandlers.AdminDeleteServiceCoverPhoto).RequireAuthorization("AdminOnly");

        // Service Packages (+ thumbnail + inclusions bulk-set)
        group.MapGet("/service-packages", ServiceCatalogHandlers.GetServicePackages);
        group.MapGet("/service-packages/{id:guid}", ServiceCatalogHandlers.GetServicePackageById);
        group.MapPost("/service-packages", ServiceCatalogHandlers.AdminCreateServicePackage).RequireAuthorization("AdminOnly");
        group.MapPut("/service-packages/{id:guid}", ServiceCatalogHandlers.AdminUpdateServicePackage).RequireAuthorization("AdminOnly");
        group.MapDelete("/service-packages/{id:guid}", ServiceCatalogHandlers.AdminDeleteServicePackage).RequireAuthorization("AdminOnly");
        group.MapPost("/service-packages/{id:guid}/thumbnail", ServiceCatalogHandlers.AdminUploadPackageThumbnail).RequireAuthorization("AdminOnly").DisableAntiforgery();
        group.MapDelete("/service-packages/{id:guid}/thumbnail", ServiceCatalogHandlers.AdminDeletePackageThumbnail).RequireAuthorization("AdminOnly");
        group.MapPut("/service-packages/{id:guid}/inclusions", ServiceCatalogHandlers.AdminSetPackageInclusions).RequireAuthorization("AdminOnly");

        // Inclusions
        group.MapGet("/inclusions", ServiceCatalogHandlers.GetInclusions);
        group.MapGet("/inclusions/{id:guid}", ServiceCatalogHandlers.GetInclusionById);
        group.MapPost("/inclusions", ServiceCatalogHandlers.AdminCreateInclusion).RequireAuthorization("AdminOnly");
        group.MapPut("/inclusions/{id:guid}", ServiceCatalogHandlers.AdminUpdateInclusion).RequireAuthorization("AdminOnly");
        group.MapDelete("/inclusions/{id:guid}", ServiceCatalogHandlers.AdminDeleteInclusion).RequireAuthorization("AdminOnly");

        return group;
    }
}
