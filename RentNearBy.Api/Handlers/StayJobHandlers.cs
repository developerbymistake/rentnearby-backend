using System.Security.Claims;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

public static class StayJobHandlers
{
    private const int DefaultPageSize = 10;
    private const int MaxPageSize = 30;
    private const string JobNotFound = "Job not found";

    private static IResult StayNotLive() =>
        ConflictResponse("Your stay must be live to post a job.", StayErrorTypes.StayNotApproved);

    private static IResult JobClosed() =>
        ConflictResponse("This job is closed.", StayJobErrorTypes.StayJobClosed);

    private static IResult InvalidLookup(string label) =>
        BadRequestResponse($"Pick a valid {label}.", StayJobErrorTypes.InvalidJobLookup);

    // ---- Lookups (shared with the admin side) ----

    internal static JobLookupItemDto ToItem(JobLookupBase x) => new()
    {
        Id = x.Id,
        Name = x.Name,
        SortOrder = x.SortOrder,
        IsActive = x.IsActive,
        JobDepartmentId = (x as JobRole)?.JobDepartmentId,
    };

    private static async Task<List<JobLookupItemDto>> ListAsync<T>(IQueryable<T> query, bool activeOnly) where T : JobLookupBase
    {
        if (activeOnly) query = query.Where(x => x.IsActive);
        return await query
            .AsNoTracking()
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Name)
            .Select(x => new JobLookupItemDto { Id = x.Id, Name = x.Name, SortOrder = x.SortOrder, IsActive = x.IsActive })
            .ToListAsync();
    }

    internal static async Task<StayJobLookupsDto> LoadLookupsAsync(ApplicationDbContext db, bool activeOnly)
    {
        var roles = db.JobRoles.AsNoTracking();
        if (activeOnly) roles = roles.Where(r => r.IsActive && r.Department.IsActive);

        return new StayJobLookupsDto
        {
            Departments = await ListAsync(db.JobDepartments, activeOnly),
            Roles = await roles
                .OrderBy(r => r.SortOrder)
                .ThenBy(r => r.Name)
                .Select(r => new JobLookupItemDto
                {
                    Id = r.Id, Name = r.Name, SortOrder = r.SortOrder, IsActive = r.IsActive, JobDepartmentId = r.JobDepartmentId,
                })
                .ToListAsync(),
            JobTypes = await ListAsync(db.JobTypes, activeOnly),
            ShiftTypes = await ListAsync(db.JobShiftTypes, activeOnly),
            SalaryTypes = await ListAsync(db.JobSalaryTypes, activeOnly),
        };
    }

    // Returns the label of the first invalid pick, or null when all are valid and active.
    private static async Task<string?> CheckLookupsAsync(UpsertStayJobRequest r, ApplicationDbContext db)
    {
        if (!await db.JobDepartments.AnyAsync(d => d.Id == r.JobDepartmentId && d.IsActive)) return "department";
        if (!await db.JobRoles.AnyAsync(x => x.Id == r.JobRoleId && x.IsActive && x.JobDepartmentId == r.JobDepartmentId)) return "role";
        if (!await db.JobTypes.AnyAsync(x => x.Id == r.JobTypeId && x.IsActive)) return "job type";
        if (!await db.JobShiftTypes.AnyAsync(x => x.Id == r.JobShiftTypeId && x.IsActive)) return "shift";
        if (!await db.JobSalaryTypes.AnyAsync(x => x.Id == r.JobSalaryTypeId && x.IsActive)) return "salary type";
        return null;
    }

    private static void Apply(StayJob job, UpsertStayJobRequest r)
    {
        job.Title = r.Title.Trim();
        job.Description = r.Description.Trim();
        job.JobDepartmentId = r.JobDepartmentId;
        job.JobRoleId = r.JobRoleId;
        job.JobTypeId = r.JobTypeId;
        job.JobShiftTypeId = r.JobShiftTypeId;
        job.JobSalaryTypeId = r.JobSalaryTypeId;
        job.MinimumExperienceYears = r.MinimumExperienceYears!.Value;
        job.MinSalary = r.MinSalary;
        job.MaxSalary = r.MaxSalary;
        job.ContactPhone = r.ContactPhone.Trim();
        job.WhatsAppNumber = r.WhatsAppNumber.Trim();
    }

    private static async Task<StayJobDetailDto?> OwnerDetailAsync(Guid id, Guid userId, IUnitOfWork unitOfWork)
    {
        var dto = await unitOfWork.StayJobs.GetOwnerDetailAsync(id, userId);
        if (dto != null) dto.IsOwner = true;
        return dto;
    }

    // ---- Public ----

    public static async Task<IResult> GetLookups(ApplicationDbContext db)
        => OkResponse(await LoadLookupsAsync(db, activeOnly: true));

    public static async Task<IResult> GetBrowse(
        Guid districtId, Guid? cityId, Guid? departmentId, int? minExperience, int? maxExperience, int? minSalary, int? maxSalary,
        IUnitOfWork unitOfWork, int page = 1, int pageSize = DefaultPageSize)
    {
        var size = Math.Clamp(pageSize <= 0 ? DefaultPageSize : pageSize, 1, MaxPageSize);
        var pageNo = Math.Max(page, 1);
        int? minExp = minExperience.HasValue
            ? Math.Clamp(minExperience.Value, StayJobRules.MinExperienceYears, StayJobRules.MaxExperienceYears) : null;
        int? maxExp = maxExperience.HasValue
            ? Math.Clamp(maxExperience.Value, StayJobRules.MinExperienceYears, StayJobRules.MaxExperienceYears) : null;
        var (items, hasMore) = await unitOfWork.StayJobs.BrowseAsync(
            districtId, cityId, departmentId, minExp, maxExp, minSalary, maxSalary, pageNo, size);
        return OkResponse(new { items, hasMore });
    }

    public static async Task<IResult> GetById(Guid id, IUnitOfWork unitOfWork, ClaimsPrincipal principal)
    {
        var isAuth = principal.Identity?.IsAuthenticated == true;
        var hasUser = UsersHandlers.TryGetUserId(principal, out var userId) && isAuth;

        var dto = await unitOfWork.StayJobs.GetPublicDetailAsync(id);
        if (dto == null && hasUser) dto = await unitOfWork.StayJobs.GetOwnerDetailAsync(id, userId);
        if (dto == null) return NotFoundResponse(JobNotFound);

        dto.IsOwner = hasUser && dto.OwnerUserId == userId;
        if (!hasUser)
        {
            dto.ContactPhone = null;
            dto.WhatsAppNumber = null;
        }
        else
        {
            dto.HasReportedStay = await unitOfWork.ListingReports.HasPendingReportFromReporterAsync(
                dto.StayListingId, ListingKinds.Stay, userId);
        }
        return OkResponse(dto);
    }

    // ---- Owner (CanListStays) ----

    public static async Task<IResult> GetMine(ClaimsPrincipal principal, IUnitOfWork unitOfWork)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var items = await unitOfWork.StayJobs.GetOwnerListAsync(userId);
        var activeCount = items.Count(j => j.Status == StayJobStatuses.Active);
        var stayStatus = await unitOfWork.Stays.GetStatusByUserAsync(userId);

        return OkResponse(new MyStayJobsDto
        {
            Items = items,
            ActiveCount = activeCount,
            MaxActive = StayJobRules.MaxActiveJobsPerOwner,
            StayStatus = stayStatus,
            CanPost = stayStatus == StayStatuses.Approved && activeCount < StayJobRules.MaxActiveJobsPerOwner,
        });
    }

    public static async Task<IResult> GetMineById(Guid id, ClaimsPrincipal principal, IUnitOfWork unitOfWork)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var dto = await OwnerDetailAsync(id, userId, unitOfWork);
        return dto == null ? NotFoundResponse(JobNotFound) : OkResponse(dto);
    }

    // Live on post. The FOR UPDATE lock on the stay row serialises parallel creates so the
    // active-job cap cannot be passed twice. Seeker and admin pushes are best-effort after commit.
    public static async Task<IResult> Create(
        UpsertStayJobRequest request,
        IValidator<UpsertStayJobRequest> validator,
        ClaimsPrincipal principal,
        IUnitOfWork unitOfWork,
        ApplicationDbContext db,
        IRabbitMqPublisher publisher,
        ILoggerFactory loggerFactory)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var stay = await unitOfWork.Stays.GetLiveByUserAsync(userId);
        if (stay == null || stay.Status != StayStatuses.Approved) return StayNotLive();

        var invalid = await CheckLookupsAsync(request, db);
        if (invalid != null) return InvalidLookup(invalid);

        var now = DateTime.UtcNow;
        var job = new StayJob
        {
            Id = Guid.NewGuid(),
            StayListingId = stay.Id,
            UserId = userId,
            Status = StayJobStatuses.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };
        Apply(job, request);

        await unitOfWork.BeginTransactionAsync();
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"StayListings\" WHERE \"Id\" = {stay.Id} FOR UPDATE");

            if (await unitOfWork.StayJobs.CountActiveByUserAsync(userId) >= StayJobRules.MaxActiveJobsPerOwner)
            {
                await unitOfWork.RollbackTransactionAsync();
                return ConflictResponse(StayJobRules.LimitMessage, StayJobErrorTypes.StayJobLimit);
            }

            await unitOfWork.StayJobs.AddAsync(job);
            await unitOfWork.SaveChangesAsync();
            await unitOfWork.CommitTransactionAsync();
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }

        await StayJobPostedPublisher.PublishAsync(publisher, job.Id, loggerFactory.CreateLogger(nameof(StayJobHandlers)));

        var dto = await OwnerDetailAsync(job.Id, userId, unitOfWork);
        return CreatedResponse(dto, $"/api/v1/stay-jobs/{job.Id}");
    }

    public static async Task<IResult> Update(
        Guid id,
        UpsertStayJobRequest request,
        IValidator<UpsertStayJobRequest> validator,
        ClaimsPrincipal principal,
        IUnitOfWork unitOfWork,
        ApplicationDbContext db)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var job = await unitOfWork.StayJobs.GetOwnedTrackedAsync(id, userId);
        if (job == null) return NotFoundResponse(JobNotFound);
        if (job.Status != StayJobStatuses.Active) return JobClosed();

        var stayStatus = await unitOfWork.Stays.GetStatusByUserAsync(userId);
        if (stayStatus != StayStatuses.Approved) return StayNotLive();

        var invalid = await CheckLookupsAsync(request, db);
        if (invalid != null) return InvalidLookup(invalid);

        Apply(job, request);
        job.UpdatedAt = DateTime.UtcNow;
        await unitOfWork.SaveChangesAsync();

        return OkResponse(await OwnerDetailAsync(job.Id, userId, unitOfWork));
    }

    // Closed is terminal; closing twice just returns the job.
    public static async Task<IResult> Close(Guid id, ClaimsPrincipal principal, IUnitOfWork unitOfWork)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var job = await unitOfWork.StayJobs.GetOwnedTrackedAsync(id, userId);
        if (job == null) return NotFoundResponse(JobNotFound);

        if (job.Status == StayJobStatuses.Active)
        {
            var now = DateTime.UtcNow;
            job.Status = StayJobStatuses.Closed;
            job.ClosedAt = now;
            job.UpdatedAt = now;
            await unitOfWork.SaveChangesAsync();
        }

        return OkResponse(await OwnerDetailAsync(job.Id, userId, unitOfWork));
    }
}
