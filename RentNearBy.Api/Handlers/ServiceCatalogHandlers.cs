using FluentValidation;
using Mapster;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

// Section/Category/Service/Package/Inclusion CRUD for the Local Services Marketplace / Expert
// Consultations catalog. GET handlers are dual-mounted under both the consumer-facing "/services/..."
// group and the admin "/admin/service-..." group (same handler, no active-only filtering server-side —
// mirrors AdminHandlers.GetDistricts/GetCities being mounted under both /admin/districts and
// /listings/locations/districts). Mutations are admin-only.
public static class ServiceCatalogHandlers
{
    private const long MaxImageBytes = 10 * 1024 * 1024;

    // ── Service Sections ────────────────────────────────────────────────────

    public static async Task<IResult> GetServiceSections(IUnitOfWork unitOfWork)
    {
        var sections = await unitOfWork.ServiceSections.GetAllOrderedAsync();
        return OkResponse(sections.Select(s => s.Adapt<ServiceSectionDto>()));
    }

    public static async Task<IResult> GetServiceSectionById(Guid id, IUnitOfWork unitOfWork)
    {
        var section = await unitOfWork.ServiceSections.GetByIdAsync(id);
        if (section == null) return NotFoundResponse("Service section not found");
        return OkResponse(section.Adapt<ServiceSectionDto>());
    }

    public static async Task<IResult> AdminCreateServiceSection(
        CreateServiceSectionRequest request, IValidator<CreateServiceSectionRequest> validator, IUnitOfWork unitOfWork)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var section = new ServiceSection
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            IconName = request.IconName.Trim(),
            SortOrder = request.SortOrder,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };

        await unitOfWork.ServiceSections.AddAsync(section);
        await unitOfWork.SaveChangesAsync();

        return CreatedResponse(section.Adapt<ServiceSectionDto>(), $"/api/v1/admin/service-sections/{section.Id}");
    }

    public static async Task<IResult> AdminUpdateServiceSection(
        Guid id, UpdateServiceSectionRequest request, IValidator<UpdateServiceSectionRequest> validator, IUnitOfWork unitOfWork)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var section = await unitOfWork.ServiceSections.GetByIdAsync(id);
        if (section == null) return NotFoundResponse("Service section not found");

        if (request.Name != null) section.Name = request.Name.Trim();
        if (request.IconName != null) section.IconName = request.IconName.Trim();
        if (request.SortOrder.HasValue) section.SortOrder = request.SortOrder.Value;
        if (request.IsActive.HasValue) section.IsActive = request.IsActive.Value;

        await unitOfWork.SaveChangesAsync();
        return OkResponse(section.Adapt<ServiceSectionDto>());
    }

    public static async Task<IResult> AdminDeleteServiceSection(Guid id, IUnitOfWork unitOfWork)
    {
        var section = await unitOfWork.ServiceSections.GetByIdAsync(id);
        if (section == null) return NotFoundResponse("Service section not found");

        var categories = await unitOfWork.ServiceCategories.GetByServiceSectionIdAsync(id);
        if (categories.Any())
            return ConflictResponse("Cannot delete a section that still has categories. Delete or move its categories first.");

        await unitOfWork.ServiceSections.DeleteAsync(section);
        await unitOfWork.SaveChangesAsync();
        return NoContentResponse();
    }

    // ── Service Categories ──────────────────────────────────────────────────

    public static async Task<IResult> GetServiceCategories(Guid? serviceSectionId, IUnitOfWork unitOfWork)
    {
        var categories = await unitOfWork.ServiceCategories.GetByServiceSectionIdAsync(serviceSectionId);
        return OkResponse(categories.Select(c => c.Adapt<ServiceCategoryDto>()));
    }

    public static async Task<IResult> GetServiceCategoryById(Guid id, IUnitOfWork unitOfWork)
    {
        var category = await unitOfWork.ServiceCategories.GetByIdAsync(id);
        if (category == null) return NotFoundResponse("Service category not found");
        return OkResponse(category.Adapt<ServiceCategoryDto>());
    }

    public static async Task<IResult> AdminCreateServiceCategory(
        CreateServiceCategoryRequest request, IValidator<CreateServiceCategoryRequest> validator, IUnitOfWork unitOfWork)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var section = await unitOfWork.ServiceSections.GetByIdAsync(request.ServiceSectionId);
        if (section == null) return NotFoundResponse("Service section not found");

        var category = new ServiceCategory
        {
            Id = Guid.NewGuid(),
            ServiceSectionId = request.ServiceSectionId,
            Name = request.Name.Trim(),
            IconName = request.IconName.Trim(),
            SortOrder = request.SortOrder,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };

        await unitOfWork.ServiceCategories.AddAsync(category);
        await unitOfWork.SaveChangesAsync();

        return CreatedResponse(category.Adapt<ServiceCategoryDto>(), $"/api/v1/admin/service-categories/{category.Id}");
    }

    public static async Task<IResult> AdminUpdateServiceCategory(
        Guid id, UpdateServiceCategoryRequest request, IValidator<UpdateServiceCategoryRequest> validator, IUnitOfWork unitOfWork)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var category = await unitOfWork.ServiceCategories.GetByIdAsync(id);
        if (category == null) return NotFoundResponse("Service category not found");

        if (request.Name != null) category.Name = request.Name.Trim();
        if (request.IconName != null) category.IconName = request.IconName.Trim();
        if (request.SortOrder.HasValue) category.SortOrder = request.SortOrder.Value;
        if (request.IsActive.HasValue) category.IsActive = request.IsActive.Value;

        await unitOfWork.SaveChangesAsync();
        return OkResponse(category.Adapt<ServiceCategoryDto>());
    }

    public static async Task<IResult> AdminDeleteServiceCategory(Guid id, IUnitOfWork unitOfWork)
    {
        var category = await unitOfWork.ServiceCategories.GetByIdAsync(id);
        if (category == null) return NotFoundResponse("Service category not found");

        var services = await unitOfWork.Services.GetByServiceCategoryIdAsync(id);
        if (services.Any())
            return ConflictResponse("Cannot delete a category that still has services. Delete or move its services first.");

        // AgentServiceCategory rows for this category cascade-delete automatically (FK Cascade) — no
        // pre-check needed, this just unassigns agents from the deleted category.
        await unitOfWork.ServiceCategories.DeleteAsync(category);
        await unitOfWork.SaveChangesAsync();
        return NoContentResponse();
    }

    // ── Services ─────────────────────────────────────────────────────────────

    public static async Task<IResult> GetServices(Guid? serviceCategoryId, IUnitOfWork unitOfWork)
    {
        var services = await unitOfWork.Services.GetByServiceCategoryIdAsync(serviceCategoryId);
        return OkResponse(services.Select(s => s.Adapt<ServiceListItemDto>()));
    }

    // Home-rail preview — pre-sorted (featured first, then SortOrder) and capped server-side, so the
    // client renders exactly what it's given instead of fetching the whole catalog and slicing it.
    public static async Task<IResult> GetServicesPreview(Guid serviceSectionId, int? limit, IUnitOfWork unitOfWork)
    {
        var services = await unitOfWork.Services.GetPreviewByServiceSectionIdAsync(serviceSectionId, limit ?? 6);
        return OkResponse(services.Select(s => s.Adapt<ServiceListItemDto>()));
    }

    public static async Task<IResult> GetServiceById(Guid id, IUnitOfWork unitOfWork)
    {
        var service = await unitOfWork.Services.GetByIdWithDetailsAsync(id);
        if (service == null) return NotFoundResponse("Service not found");
        return OkResponse(service.Adapt<ServiceDetailDto>());
    }

    public static async Task<IResult> AdminCreateService(
        CreateServiceRequest request, IValidator<CreateServiceRequest> validator, IUnitOfWork unitOfWork)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var category = await unitOfWork.ServiceCategories.GetByIdAsync(request.ServiceCategoryId);
        if (category == null) return NotFoundResponse("Service category not found");

        var service = new Service
        {
            Id = Guid.NewGuid(),
            ServiceCategoryId = request.ServiceCategoryId,
            Name = request.Name.Trim(),
            IconName = request.IconName.Trim(),
            ShortDescription = request.ShortDescription.Trim(),
            FullDescription = request.FullDescription.Trim(),
            CoverPhotoUrl = string.Empty,
            CoverPhotoFilePath = string.Empty,
            SortOrder = request.SortOrder,
            IsFeatured = request.IsFeatured,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        await unitOfWork.Services.AddAsync(service);
        await unitOfWork.SaveChangesAsync();

        return CreatedResponse(service.Adapt<ServiceListItemDto>(), $"/api/v1/admin/services/{service.Id}");
    }

    public static async Task<IResult> AdminUpdateService(
        Guid id, UpdateServiceRequest request, IValidator<UpdateServiceRequest> validator, IUnitOfWork unitOfWork)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var service = await unitOfWork.Services.GetByIdAsync(id);
        if (service == null) return NotFoundResponse("Service not found");

        if (request.Name != null) service.Name = request.Name.Trim();
        if (request.IconName != null) service.IconName = request.IconName.Trim();
        if (request.ShortDescription != null) service.ShortDescription = request.ShortDescription.Trim();
        if (request.FullDescription != null) service.FullDescription = request.FullDescription.Trim();
        if (request.SortOrder.HasValue) service.SortOrder = request.SortOrder.Value;
        if (request.IsFeatured.HasValue) service.IsFeatured = request.IsFeatured.Value;
        if (request.IsActive.HasValue) service.IsActive = request.IsActive.Value;
        service.UpdatedAt = DateTime.UtcNow;

        await unitOfWork.SaveChangesAsync();

        var updated = await unitOfWork.Services.GetByIdWithDetailsAsync(id);
        return OkResponse(updated!.Adapt<ServiceDetailDto>());
    }

    public static async Task<IResult> AdminDeleteService(
        Guid id, IUnitOfWork unitOfWork, IPhotoService photoService, ApplicationDbContext db)
    {
        var service = await unitOfWork.Services.GetByIdWithDetailsAsync(id);
        if (service == null) return NotFoundResponse("Service not found");

        // Inquiry.ServiceId's FK is Restrict (same reasoning as ExistsByServicePackageIdAsync) — any
        // referencing inquiry, terminal status or not, would make the DB cascade-delete of this
        // Service's packages throw. Pre-check so the admin gets a clean 409, not a raw DB exception.
        if (await db.Inquiries.AnyAsync(i => i.ServiceId == id))
            return ConflictResponse("Cannot delete a service that has inquiries. Deactivate it instead.");

        // Hard-delete order mirrors AdminDeleteBanner: Cloudinary asset(s) deleted BEFORE the DB row(s).
        // The Service row's own cover photo, then every child package's thumbnail (DB cascade will
        // delete the ServicePackage rows themselves, but it does NOT know about their Cloudinary
        // assets — those must be cleaned up here first).
        if (!string.IsNullOrEmpty(service.CoverPhotoFilePath))
            await photoService.DeletePhotoAsync(service.CoverPhotoFilePath);

        foreach (var package in service.Packages)
        {
            if (!string.IsNullOrEmpty(package.ThumbnailFilePath))
                await photoService.DeletePhotoAsync(package.ThumbnailFilePath);
        }

        var tracked = await unitOfWork.Services.GetByIdAsync(id);
        await unitOfWork.Services.DeleteAsync(tracked!);
        await unitOfWork.SaveChangesAsync();
        return NoContentResponse();
    }

    public static async Task<IResult> AdminUploadServiceCoverPhoto(
        Guid id, IFormFile image, IUnitOfWork unitOfWork, IPhotoService photoService)
    {
        var service = await unitOfWork.Services.GetByIdAsync(id);
        if (service == null) return NotFoundResponse("Service not found");
        if (image.Length > MaxImageBytes) return BadRequestResponse("Image size must not exceed 10MB");

        // Replace-in-place composite flow: delete the old Cloudinary asset (if any) BEFORE saving the
        // new one, then update the same row — never leaves two live assets for one photo slot.
        if (!string.IsNullOrEmpty(service.CoverPhotoFilePath))
            await photoService.DeletePhotoAsync(service.CoverPhotoFilePath);

        using var stream = image.OpenReadStream();
        var (url, filePath) = await photoService.SaveServiceCoverPhotoAsync(stream, image.FileName, id);

        service.CoverPhotoUrl = url;
        service.CoverPhotoFilePath = filePath;
        service.UpdatedAt = DateTime.UtcNow;
        await unitOfWork.SaveChangesAsync();

        return OkResponse(new { coverPhotoUrl = url });
    }

    public static async Task<IResult> AdminDeleteServiceCoverPhoto(
        Guid id, IUnitOfWork unitOfWork, IPhotoService photoService)
    {
        var service = await unitOfWork.Services.GetByIdAsync(id);
        if (service == null) return NotFoundResponse("Service not found");
        if (string.IsNullOrEmpty(service.CoverPhotoFilePath)) return BadRequestResponse("No cover photo to delete");

        await photoService.DeletePhotoAsync(service.CoverPhotoFilePath);
        service.CoverPhotoUrl = string.Empty;
        service.CoverPhotoFilePath = string.Empty;
        service.UpdatedAt = DateTime.UtcNow;
        await unitOfWork.SaveChangesAsync();

        return NoContentResponse();
    }

    // ── Service Packages ─────────────────────────────────────────────────────

    public static async Task<IResult> GetServicePackages(Guid? serviceId, IUnitOfWork unitOfWork)
    {
        var packages = await unitOfWork.ServicePackages.GetByServiceIdAsync(serviceId);
        return OkResponse(packages.Select(p => p.Adapt<ServicePackageDto>()));
    }

    public static async Task<IResult> GetServicePackageById(Guid id, IUnitOfWork unitOfWork)
    {
        var package = await unitOfWork.ServicePackages.GetByIdWithInclusionsAsync(id);
        if (package == null) return NotFoundResponse("Service package not found");
        return OkResponse(package.Adapt<ServicePackageDto>());
    }

    public static async Task<IResult> AdminCreateServicePackage(
        CreateServicePackageRequest request, IValidator<CreateServicePackageRequest> validator, IUnitOfWork unitOfWork)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var service = await unitOfWork.Services.GetByIdAsync(request.ServiceId);
        if (service == null) return NotFoundResponse("Service not found");

        var package = new ServicePackage
        {
            Id = Guid.NewGuid(),
            ServiceId = request.ServiceId,
            Name = request.Name.Trim(),
            Price = request.Price,
            OriginalPrice = request.OriginalPrice,
            DiscountPercent = request.DiscountPercent,
            IsStartingAtPrice = request.IsStartingAtPrice,
            DurationDays = request.DurationDays,
            DurationNights = request.DurationNights,
            PriceUnit = string.IsNullOrWhiteSpace(request.PriceUnit) ? null : request.PriceUnit.Trim(),
            ThumbnailUrl = string.Empty,
            ThumbnailFilePath = string.Empty,
            SortOrder = request.SortOrder,
            IsFeatured = request.IsFeatured,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        await unitOfWork.ServicePackages.AddAsync(package);
        await unitOfWork.SaveChangesAsync();

        return CreatedResponse(package.Adapt<ServicePackageDto>(), $"/api/v1/admin/service-packages/{package.Id}");
    }

    public static async Task<IResult> AdminUpdateServicePackage(
        Guid id, UpdateServicePackageRequest request, IValidator<UpdateServicePackageRequest> validator, IUnitOfWork unitOfWork)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var package = await unitOfWork.ServicePackages.GetByIdAsync(id);
        if (package == null) return NotFoundResponse("Service package not found");

        if (request.Name != null) package.Name = request.Name.Trim();

        // Pricing trio + IsStartingAtPrice are applied as one atomic group, never patched
        // independently: Price/OriginalPrice/DiscountPercent are each individually nullable at the
        // entity level (null is a legitimate "Get Custom Quote" value, not "leave unchanged"), so the
        // only unambiguous signal that the admin form submitted a pricing edit at all is ANY of the
        // four fields being non-null. When that's true, all four are written verbatim (including
        // nulling out ones the request left null) — e.g. switching to "Get Custom Quote" sends
        // IsStartingAtPrice=false with Price/OriginalPrice/DiscountPercent all null.
        var pricingTouched = request.Price.HasValue || request.OriginalPrice.HasValue
            || request.DiscountPercent.HasValue || request.IsStartingAtPrice.HasValue;
        if (pricingTouched)
        {
            package.Price = request.Price;
            package.OriginalPrice = request.OriginalPrice;
            package.DiscountPercent = request.DiscountPercent;
            package.IsStartingAtPrice = request.IsStartingAtPrice ?? false;
        }

        if (request.DurationDays.HasValue) package.DurationDays = request.DurationDays.Value;
        if (request.DurationNights.HasValue) package.DurationNights = request.DurationNights.Value;
        if (request.PriceUnit != null)
            package.PriceUnit = string.IsNullOrWhiteSpace(request.PriceUnit) ? null : request.PriceUnit.Trim();
        if (request.SortOrder.HasValue) package.SortOrder = request.SortOrder.Value;
        if (request.IsFeatured.HasValue) package.IsFeatured = request.IsFeatured.Value;
        if (request.IsActive.HasValue) package.IsActive = request.IsActive.Value;
        package.UpdatedAt = DateTime.UtcNow;

        await unitOfWork.SaveChangesAsync();

        var updated = await unitOfWork.ServicePackages.GetByIdWithInclusionsAsync(id);
        return OkResponse(updated!.Adapt<ServicePackageDto>());
    }

    public static async Task<IResult> AdminDeleteServicePackage(
        Guid id, IUnitOfWork unitOfWork, IPhotoService photoService)
    {
        var package = await unitOfWork.ServicePackages.GetByIdAsync(id);
        if (package == null) return NotFoundResponse("Service package not found");

        if (await unitOfWork.Inquiries.ExistsByServicePackageIdAsync(id))
            return ConflictResponse("Cannot delete a package that has inquiries. Deactivate it instead.");

        if (!string.IsNullOrEmpty(package.ThumbnailFilePath))
            await photoService.DeletePhotoAsync(package.ThumbnailFilePath);

        await unitOfWork.ServicePackages.DeleteAsync(package);
        await unitOfWork.SaveChangesAsync();
        return NoContentResponse();
    }

    public static async Task<IResult> AdminUploadPackageThumbnail(
        Guid id, IFormFile image, IUnitOfWork unitOfWork, IPhotoService photoService)
    {
        var package = await unitOfWork.ServicePackages.GetByIdAsync(id);
        if (package == null) return NotFoundResponse("Service package not found");
        if (image.Length > MaxImageBytes) return BadRequestResponse("Image size must not exceed 10MB");

        if (!string.IsNullOrEmpty(package.ThumbnailFilePath))
            await photoService.DeletePhotoAsync(package.ThumbnailFilePath);

        using var stream = image.OpenReadStream();
        var (url, filePath) = await photoService.SavePackageThumbnailAsync(stream, image.FileName, id);

        package.ThumbnailUrl = url;
        package.ThumbnailFilePath = filePath;
        package.UpdatedAt = DateTime.UtcNow;
        await unitOfWork.SaveChangesAsync();

        return OkResponse(new { thumbnailUrl = url });
    }

    public static async Task<IResult> AdminDeletePackageThumbnail(
        Guid id, IUnitOfWork unitOfWork, IPhotoService photoService)
    {
        var package = await unitOfWork.ServicePackages.GetByIdAsync(id);
        if (package == null) return NotFoundResponse("Service package not found");
        if (string.IsNullOrEmpty(package.ThumbnailFilePath)) return BadRequestResponse("No thumbnail to delete");

        await photoService.DeletePhotoAsync(package.ThumbnailFilePath);
        package.ThumbnailUrl = string.Empty;
        package.ThumbnailFilePath = string.Empty;
        package.UpdatedAt = DateTime.UtcNow;
        await unitOfWork.SaveChangesAsync();

        return NoContentResponse();
    }

    public static async Task<IResult> AdminSetPackageInclusions(
        Guid id, SetPackageInclusionsRequest request, IValidator<SetPackageInclusionsRequest> validator,
        IUnitOfWork unitOfWork, ApplicationDbContext db)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var package = await unitOfWork.ServicePackages.GetByIdAsync(id);
        if (package == null) return NotFoundResponse("Service package not found");

        var distinctIds = request.InclusionIds.Distinct().ToList();
        if (distinctIds.Count > 0)
        {
            var validCount = await db.Inclusions.CountAsync(i => distinctIds.Contains(i.Id));
            if (validCount != distinctIds.Count)
                return BadRequestResponse("One or more InclusionIds are invalid");
        }

        // Full-replace, exact mirror of how BannerHandlers manipulates db.BannerDismissals directly:
        // RemoveRange + AddRange + one SaveChangesAsync, no diffing.
        var existing = db.PackageInclusions.Where(pi => pi.ServicePackageId == id);
        db.PackageInclusions.RemoveRange(existing);
        db.PackageInclusions.AddRange(distinctIds.Select(inclusionId => new PackageInclusion
        {
            ServicePackageId = id,
            InclusionId = inclusionId,
        }));
        await db.SaveChangesAsync();

        var updated = await unitOfWork.ServicePackages.GetByIdWithInclusionsAsync(id);
        return OkResponse(updated!.Adapt<ServicePackageDto>());
    }

    // ── Inclusions ───────────────────────────────────────────────────────────

    public static async Task<IResult> GetInclusions(IUnitOfWork unitOfWork)
    {
        var inclusions = await unitOfWork.Inclusions.GetAllOrderedAsync();
        return OkResponse(inclusions.Select(i => i.Adapt<InclusionDto>()));
    }

    public static async Task<IResult> GetInclusionById(Guid id, IUnitOfWork unitOfWork)
    {
        var inclusion = await unitOfWork.Inclusions.GetByIdAsync(id);
        if (inclusion == null) return NotFoundResponse("Inclusion not found");
        return OkResponse(inclusion.Adapt<InclusionDto>());
    }

    public static async Task<IResult> AdminCreateInclusion(
        CreateInclusionRequest request, IValidator<CreateInclusionRequest> validator, IUnitOfWork unitOfWork)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var inclusion = new Inclusion
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            IconName = request.IconName.Trim(),
            SortOrder = request.SortOrder,
            IsActive = true,
        };

        await unitOfWork.Inclusions.AddAsync(inclusion);
        await unitOfWork.SaveChangesAsync();

        return CreatedResponse(inclusion.Adapt<InclusionDto>(), $"/api/v1/admin/inclusions/{inclusion.Id}");
    }

    public static async Task<IResult> AdminUpdateInclusion(
        Guid id, UpdateInclusionRequest request, IValidator<UpdateInclusionRequest> validator, IUnitOfWork unitOfWork)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var inclusion = await unitOfWork.Inclusions.GetByIdAsync(id);
        if (inclusion == null) return NotFoundResponse("Inclusion not found");

        if (request.Name != null) inclusion.Name = request.Name.Trim();
        if (request.IconName != null) inclusion.IconName = request.IconName.Trim();
        if (request.SortOrder.HasValue) inclusion.SortOrder = request.SortOrder.Value;
        if (request.IsActive.HasValue) inclusion.IsActive = request.IsActive.Value;

        await unitOfWork.SaveChangesAsync();
        return OkResponse(inclusion.Adapt<InclusionDto>());
    }

    public static async Task<IResult> AdminDeleteInclusion(Guid id, IUnitOfWork unitOfWork)
    {
        var inclusion = await unitOfWork.Inclusions.GetByIdAsync(id);
        if (inclusion == null) return NotFoundResponse("Inclusion not found");

        // PackageInclusion rows referencing this Inclusion cascade-delete automatically (FK Cascade) —
        // deleting a master Inclusion just removes it from whatever packages had it checked, matching
        // the plan's hard-delete-blocked list (Service/Package/Agent only, not Inclusion).
        await unitOfWork.Inclusions.DeleteAsync(inclusion);
        await unitOfWork.SaveChangesAsync();
        return NoContentResponse();
    }
}
