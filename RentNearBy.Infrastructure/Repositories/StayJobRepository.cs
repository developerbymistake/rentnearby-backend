using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

// Public = Active job on an Approved, not deleted stay whose owner is active. Unpublish, takedown,
// soft delete and suspension hide jobs through this one predicate; a paused stay keeps its jobs.
public class StayJobRepository(ApplicationDbContext context) : Repository<StayJob>(context), IStayJobRepository
{
    private IQueryable<StayJob> PublicJobs() => _dbSet
        .AsNoTracking()
        .Where(j => j.Status == StayJobStatuses.Active
            && context.StayListings.Any(s => s.Id == j.StayListingId
                && s.Status == StayStatuses.Approved && !s.IsDeleted && s.User.IsActive));

    public async Task<int> CountActiveByUserAsync(Guid userId)
        => await _dbSet.CountAsync(j => j.UserId == userId && j.Status == StayJobStatuses.Active);

    public async Task<StayJob?> GetOwnedTrackedAsync(Guid id, Guid userId)
        => await _dbSet.FirstOrDefaultAsync(j => j.Id == id && j.UserId == userId);

    public async Task<List<StayJobOwnerDto>> GetOwnerListAsync(Guid userId)
        => await _dbSet
            .AsNoTracking()
            .Where(j => j.UserId == userId)
            .OrderBy(j => j.Status == StayJobStatuses.Active ? 0 : 1)
            .ThenByDescending(j => j.CreatedAt)
            .Select(j => new StayJobOwnerDto
            {
                Id = j.Id,
                Title = j.Title,
                StayName = context.StayListings.Where(s => s.Id == j.StayListingId).Select(s => s.Name).FirstOrDefault(),
                DepartmentName = j.Department.Name,
                RoleName = j.Role.Name,
                MinimumExperienceYears = j.MinimumExperienceYears,
                SalaryTypeName = j.SalaryType.Name,
                MinSalary = j.MinSalary,
                MaxSalary = j.MaxSalary,
                Status = j.Status,
                CreatedAt = j.CreatedAt,
                UpdatedAt = j.UpdatedAt,
                ClosedAt = j.ClosedAt,
            })
            .ToListAsync();

    public async Task<(IReadOnlyList<StayJobSummaryDto> Items, bool HasMore)> BrowseAsync(
        Guid districtId, Guid? departmentId, int? minExperience, int? maxExperience,
        int? minSalary, int? maxSalary, int page, int pageSize)
    {
        var query = PublicJobs()
            .Where(j => context.StayListings.Any(s => s.Id == j.StayListingId && s.DistrictId == districtId));
        if (departmentId.HasValue)
            query = query.Where(j => j.JobDepartmentId == departmentId.Value);
        if (minExperience.HasValue)
            query = query.Where(j => j.MinimumExperienceYears >= minExperience.Value);
        if (maxExperience.HasValue)
            query = query.Where(j => j.MinimumExperienceYears <= maxExperience.Value);
        if (minSalary.HasValue)
            query = query.Where(j => j.MaxSalary >= minSalary.Value);
        if (maxSalary.HasValue)
            query = query.Where(j => j.MinSalary <= maxSalary.Value);

        var items = await (
                from j in query
                join s in context.StayListings on j.StayListingId equals s.Id
                orderby j.CreatedAt descending, j.Id descending
                select new StayJobSummaryDto
                {
                    Id = j.Id,
                    StayListingId = s.Id,
                    StayName = s.Name,
                    StayTypeName = s.StayType != null ? s.StayType.Name : null,
                    CoverPhotoUrl = s.Photos.OrderBy(p => p.PhotoOrder).Select(p => p.PhotoUrl).FirstOrDefault(),
                    Title = j.Title,
                    DepartmentName = j.Department.Name,
                    RoleName = j.Role.Name,
                    JobTypeName = j.JobType.Name,
                    ShiftTypeName = j.ShiftType.Name,
                    SalaryTypeName = j.SalaryType.Name,
                    MinimumExperienceYears = j.MinimumExperienceYears,
                    MinSalary = j.MinSalary,
                    MaxSalary = j.MaxSalary,
                    DistrictId = s.DistrictId,
                    DistrictName = s.District != null ? s.District.Name : null,
                    CityName = s.City != null ? s.City.Name : null,
                    CreatedAt = j.CreatedAt,
                })
            .Skip((page - 1) * pageSize)
            .Take(pageSize + 1)
            .ToListAsync();

        var hasMore = items.Count > pageSize;
        return (hasMore ? items.Take(pageSize).ToList().AsReadOnly() : items.AsReadOnly(), hasMore);
    }

    public async Task<StayJobDetailDto?> GetPublicDetailAsync(Guid id)
        => await ProjectDetail(PublicJobs().Where(j => j.Id == id)).FirstOrDefaultAsync();

    public async Task<StayJobDetailDto?> GetOwnerDetailAsync(Guid id, Guid userId)
        => await ProjectDetail(_dbSet.AsNoTracking().Where(j => j.Id == id && j.UserId == userId)).FirstOrDefaultAsync();

    private IQueryable<StayJobDetailDto> ProjectDetail(IQueryable<StayJob> query) =>
        from j in query
        join s in context.StayListings on j.StayListingId equals s.Id
        select new StayJobDetailDto
        {
            Id = j.Id,
            StayListingId = s.Id,
            StayName = s.Name,
            StayTypeName = s.StayType != null ? s.StayType.Name : null,
            CoverPhotoUrl = s.Photos.OrderBy(p => p.PhotoOrder).Select(p => p.PhotoUrl).FirstOrDefault(),
            Title = j.Title,
            DepartmentName = j.Department.Name,
            RoleName = j.Role.Name,
            JobTypeName = j.JobType.Name,
            ShiftTypeName = j.ShiftType.Name,
            SalaryTypeName = j.SalaryType.Name,
            MinimumExperienceYears = j.MinimumExperienceYears,
            MinSalary = j.MinSalary,
            MaxSalary = j.MaxSalary,
            DistrictId = s.DistrictId,
            DistrictName = s.District != null ? s.District.Name : null,
            CityName = s.City != null ? s.City.Name : null,
            CreatedAt = j.CreatedAt,
            Description = j.Description,
            JobDepartmentId = j.JobDepartmentId,
            JobRoleId = j.JobRoleId,
            JobTypeId = j.JobTypeId,
            JobShiftTypeId = j.JobShiftTypeId,
            JobSalaryTypeId = j.JobSalaryTypeId,
            ContactPhone = j.ContactPhone,
            WhatsAppNumber = j.WhatsAppNumber,
            Status = j.Status,
            ClosedAt = j.ClosedAt,
            UpdatedAt = j.UpdatedAt,
            OwnerUserId = j.UserId,
        };

    public async Task<(IReadOnlyList<AdminStayJobDto> Items, bool HasMore)> GetAdminPagedAsync(
        string? status, Guid? districtId, int page, int pageSize)
    {
        var jobs = _dbSet.AsNoTracking();
        if (!string.IsNullOrEmpty(status))
            jobs = jobs.Where(j => j.Status == status);

        var query =
            from j in jobs
            join s in context.StayListings on j.StayListingId equals s.Id into stays
            from s in stays.DefaultIfEmpty()
            join u in context.Users on j.UserId equals u.Id into owners
            from u in owners.DefaultIfEmpty()
            select new { j, s, u };
        if (districtId.HasValue)
            query = query.Where(x => x.s != null && x.s.DistrictId == districtId.Value);

        var items = await query
            .OrderByDescending(x => x.j.CreatedAt)
            .ThenByDescending(x => x.j.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize + 1)
            .Select(x => new AdminStayJobDto
            {
                Id = x.j.Id,
                Title = x.j.Title,
                DepartmentName = x.j.Department.Name,
                RoleName = x.j.Role.Name,
                MinimumExperienceYears = x.j.MinimumExperienceYears,
                ContactPhone = x.j.ContactPhone,
                WhatsAppNumber = x.j.WhatsAppNumber,
                MinSalary = x.j.MinSalary,
                MaxSalary = x.j.MaxSalary,
                Status = x.j.Status,
                CreatedAt = x.j.CreatedAt,
                ClosedAt = x.j.ClosedAt,
                StayListingId = x.j.StayListingId,
                StayName = x.s != null ? x.s.Name : null,
                StayStatus = x.s != null ? x.s.Status : null,
                StayIsDeleted = x.s == null || x.s.IsDeleted,
                OwnerUserId = x.j.UserId,
                OwnerName = x.u != null ? x.u.Name : null,
                OwnerPhone = x.u != null ? x.u.PhoneNumber : null,
                DistrictName = x.s != null && x.s.District != null ? x.s.District.Name : null,
                CityName = x.s != null && x.s.City != null ? x.s.City.Name : null,
            })
            .ToListAsync();

        var hasMore = items.Count > pageSize;
        return (hasMore ? items.Take(pageSize).ToList().AsReadOnly() : items.AsReadOnly(), hasMore);
    }

    public async Task<int> CloseActiveForStayAsync(Guid stayListingId)
    {
        var now = DateTime.UtcNow;
        return await _dbSet
            .Where(j => j.StayListingId == stayListingId && j.Status == StayJobStatuses.Active)
            .ExecuteUpdateAsync(set => set
                .SetProperty(j => j.Status, StayJobStatuses.Closed)
                .SetProperty(j => j.ClosedAt, (DateTime?)now)
                .SetProperty(j => j.UpdatedAt, now));
    }
}
