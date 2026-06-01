using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

public static class BannerHandlers
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    // ── User-facing ──────────────────────────────────────────────────────────

    public static async Task<IResult> GetActiveBanner(
        Guid districtId, ClaimsPrincipal principal, IUnitOfWork unitOfWork)
    {
        if (!TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var banner = await unitOfWork.DistrictBanners.GetActiveForUserAsync(districtId, userId);
        if (banner == null) return OkResponse<DistrictBannerDto?>(null);

        return OkResponse(new DistrictBannerDto
        {
            Id = banner.Id,
            DistrictId = banner.DistrictId,
            DistrictName = string.Empty,
            ImageUrl = banner.ImageUrl,
            ContactNumber = banner.ContactNumber,
            RedirectUrl = banner.RedirectUrl,
            IsActive = banner.IsActive,
            CreatedAt = banner.CreatedAt,
        });
    }

    public static async Task<IResult> DismissBanner(
        Guid id, ClaimsPrincipal principal, ApplicationDbContext db)
    {
        if (!TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var exists = await db.BannerDismissals
            .AnyAsync(d => d.BannerId == id && d.UserId == userId);
        if (!exists)
        {
            db.BannerDismissals.Add(new BannerDismissal
            {
                UserId = userId,
                BannerId = id,
                DismissedAt = DateTime.UtcNow,
            });
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException) { /* duplicate key — already dismissed, ignore */ }
        }
        return NoContentResponse();
    }

    // ── Admin-facing ─────────────────────────────────────────────────────────

    public static async Task<IResult> AdminGetAllBanners(IUnitOfWork unitOfWork)
    {
        var banners = await unitOfWork.DistrictBanners.GetAllWithDistrictAsync();
        var dtos = banners.Select(b => new DistrictBannerDto
        {
            Id = b.Id,
            DistrictId = b.DistrictId,
            DistrictName = b.District?.Name ?? string.Empty,
            ImageUrl = b.ImageUrl,
            ContactNumber = b.ContactNumber,
            RedirectUrl = b.RedirectUrl,
            IsActive = b.IsActive,
            CreatedAt = b.CreatedAt,
        });
        return OkResponse(dtos);
    }

    public static async Task<IResult> AdminCreateBanner(
        [Microsoft.AspNetCore.Mvc.FromForm] Guid districtId,
        [Microsoft.AspNetCore.Mvc.FromForm] string? contactNumber,
        [Microsoft.AspNetCore.Mvc.FromForm] string? redirectUrl,
        IFormFile image,
        IUnitOfWork unitOfWork,
        IPhotoService photoService)
    {
        var existing = await unitOfWork.DistrictBanners.GetByDistrictIdAsync(districtId);
        if (existing != null)
            return ConflictResponse("A banner already exists for this district. Delete it first.");

        if (image.Length > 10 * 1024 * 1024)
            return BadRequestResponse("Image size must not exceed 10MB");

        using var stream = image.OpenReadStream();
        var (url, filePath) = await photoService.SaveBannerAsync(stream, image.FileName, districtId);

        var banner = new DistrictBanner
        {
            Id = Guid.NewGuid(),
            DistrictId = districtId,
            ImageUrl = url,
            ImageFilePath = filePath,
            ContactNumber = string.IsNullOrWhiteSpace(contactNumber) ? null : contactNumber.Trim(),
            RedirectUrl = string.IsNullOrWhiteSpace(redirectUrl) ? null : redirectUrl.Trim(),
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };

        await unitOfWork.DistrictBanners.AddAsync(banner);
        await unitOfWork.SaveChangesAsync();

        return CreatedResponse(new DistrictBannerDto
        {
            Id = banner.Id,
            DistrictId = banner.DistrictId,
            DistrictName = string.Empty,
            ImageUrl = banner.ImageUrl,
            ContactNumber = banner.ContactNumber,
            RedirectUrl = banner.RedirectUrl,
            IsActive = banner.IsActive,
            CreatedAt = banner.CreatedAt,
        }, $"/api/v1/admin/district-banners/{banner.Id}");
    }

    public record ToggleBannerRequest(bool IsActive);

    public static async Task<IResult> AdminToggleBanner(
        Guid id, ToggleBannerRequest request, ApplicationDbContext db)
    {
        var banner = await db.DistrictBanners.FindAsync(id);
        if (banner == null) return NotFoundResponse("Banner not found");

        banner.IsActive = request.IsActive;
        await db.SaveChangesAsync();
        return NoContentResponse();
    }

    public static async Task<IResult> AdminDeleteBanner(
        Guid id, IUnitOfWork unitOfWork, IPhotoService photoService)
    {
        var banner = await unitOfWork.DistrictBanners.GetByIdAsync(id);
        if (banner == null) return NotFoundResponse("Banner not found");

        await photoService.DeletePhotoAsync(banner.ImageFilePath);
        await unitOfWork.DistrictBanners.DeleteAsync(banner);
        await unitOfWork.SaveChangesAsync();
        return NoContentResponse();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool TryGetUserId(ClaimsPrincipal principal, out Guid userId)
    {
        userId = Guid.Empty;
        var value = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return value != null && Guid.TryParse(value, out userId);
    }
}
