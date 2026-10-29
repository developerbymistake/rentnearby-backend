using FluentValidation;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

public static class AdminStayJobHandlers
{
    private const string JobNotFound = "Job not found";
    private const string UnknownList = "Unknown list";
    private const string ItemNotFound = "Item not found";
    private const string StatusAll = "All";

    private static IResult NameTaken() => ConflictResponse("Name already exists");

    // ---- Jobs ----

    public static async Task<IResult> GetJobs(
        IUnitOfWork unitOfWork, Guid? districtId, int page = 1, int pageSize = 20, string status = StayJobStatuses.Active)
    {
        if (status != StatusAll && !StayJobStatuses.All.Contains(status)) return BadRequestResponse("Invalid status");
        pageSize = Math.Clamp(pageSize, 1, 100);
        page = Math.Max(1, page);

        var (items, hasMore) = await unitOfWork.StayJobs.GetAdminPagedAsync(
            status == StatusAll ? null : status, districtId, page, pageSize);
        return OkResponse(new { items, hasMore });
    }

    public static async Task<IResult> CloseJob(Guid id, IUnitOfWork unitOfWork)
    {
        var job = await unitOfWork.StayJobs.GetByIdAsync(id);
        if (job == null) return NotFoundResponse(JobNotFound);

        if (job.Status == StayJobStatuses.Active)
        {
            var now = DateTime.UtcNow;
            job.Status = StayJobStatuses.Closed;
            job.ClosedAt = now;
            job.UpdatedAt = now;
            await unitOfWork.SaveChangesAsync();
        }

        return OkResponse(new { success = true });
    }

    public static async Task<IResult> DeleteJob(Guid id, IUnitOfWork unitOfWork)
    {
        var job = await unitOfWork.StayJobs.GetByIdAsync(id);
        if (job == null) return NotFoundResponse(JobNotFound);

        await unitOfWork.StayJobs.DeleteAsync(job);
        await unitOfWork.SaveChangesAsync();

        return NoContentResponse();
    }

    // ---- Lookups (all rows, active or not) ----

    public static async Task<IResult> GetAllLookups(ApplicationDbContext db)
        => OkResponse(await StayJobHandlers.LoadLookupsAsync(db, activeOnly: false));

    public static async Task<IResult> CreateLookup(
        string kind, UpsertJobLookupRequest request, IValidator<UpsertJobLookupRequest> validator, ApplicationDbContext db)
    {
        if (!StayJobLookupKinds.All.Contains(kind)) return BadRequestResponse(UnknownList);

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        return kind switch
        {
            StayJobLookupKinds.Departments => await CreateAsync(db.JobDepartments, kind, request, db),
            StayJobLookupKinds.Roles => await CreateAsync(db.JobRoles, kind, request, db),
            StayJobLookupKinds.JobTypes => await CreateAsync(db.JobTypes, kind, request, db),
            StayJobLookupKinds.ShiftTypes => await CreateAsync(db.JobShiftTypes, kind, request, db),
            _ => await CreateAsync(db.JobSalaryTypes, kind, request, db),
        };
    }

    public static async Task<IResult> UpdateLookup(
        string kind, Guid id, UpsertJobLookupRequest request, IValidator<UpsertJobLookupRequest> validator, ApplicationDbContext db)
    {
        if (!StayJobLookupKinds.All.Contains(kind)) return BadRequestResponse(UnknownList);

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        return kind switch
        {
            StayJobLookupKinds.Departments => await UpdateAsync(db.JobDepartments, id, request, db),
            StayJobLookupKinds.Roles => await UpdateAsync(db.JobRoles, id, request, db),
            StayJobLookupKinds.JobTypes => await UpdateAsync(db.JobTypes, id, request, db),
            StayJobLookupKinds.ShiftTypes => await UpdateAsync(db.JobShiftTypes, id, request, db),
            _ => await UpdateAsync(db.JobSalaryTypes, id, request, db),
        };
    }

    // Hard delete only while unused; otherwise the admin turns it off with IsActive.
    public static async Task<IResult> DeleteLookup(string kind, Guid id, ApplicationDbContext db)
    {
        if (!StayJobLookupKinds.All.Contains(kind)) return BadRequestResponse(UnknownList);

        var inUse = kind switch
        {
            StayJobLookupKinds.Departments => await db.StayJobs.AnyAsync(j => j.JobDepartmentId == id)
                || await db.JobRoles.AnyAsync(r => r.JobDepartmentId == id),
            StayJobLookupKinds.Roles => await db.StayJobs.AnyAsync(j => j.JobRoleId == id),
            StayJobLookupKinds.JobTypes => await db.StayJobs.AnyAsync(j => j.JobTypeId == id),
            StayJobLookupKinds.ShiftTypes => await db.StayJobs.AnyAsync(j => j.JobShiftTypeId == id),
            _ => await db.StayJobs.AnyAsync(j => j.JobSalaryTypeId == id),
        };

        return kind switch
        {
            StayJobLookupKinds.Departments => await DeleteAsync(db.JobDepartments, id, inUse, db),
            StayJobLookupKinds.Roles => await DeleteAsync(db.JobRoles, id, inUse, db),
            StayJobLookupKinds.JobTypes => await DeleteAsync(db.JobTypes, id, inUse, db),
            StayJobLookupKinds.ShiftTypes => await DeleteAsync(db.JobShiftTypes, id, inUse, db),
            _ => await DeleteAsync(db.JobSalaryTypes, id, inUse, db),
        };
    }

    // Roles must name an existing department; names are unique per kind (roles: within the department).
    private static async Task<IResult?> CheckRoleAndNameAsync<T>(
        DbSet<T> set, Guid? excludeId, string name, UpsertJobLookupRequest request, ApplicationDbContext db)
        where T : JobLookupBase
    {
        var lower = name.ToLower();
        if (typeof(T) == typeof(JobRole))
        {
            var deptId = request.JobDepartmentId;
            if (deptId == null || deptId == Guid.Empty || !await db.JobDepartments.AnyAsync(d => d.Id == deptId.Value))
                return BadRequestResponse("Pick a department");
            if (await db.JobRoles.AnyAsync(r => r.JobDepartmentId == deptId.Value && r.Name.ToLower() == lower && r.Id != excludeId))
                return NameTaken();
            return null;
        }

        if (await set.AnyAsync(x => x.Name.ToLower() == lower && x.Id != excludeId))
            return NameTaken();
        return null;
    }

    private static async Task<IResult> CreateAsync<T>(
        DbSet<T> set, string kind, UpsertJobLookupRequest request, ApplicationDbContext db)
        where T : JobLookupBase, new()
    {
        var name = request.Name.Trim();
        var error = await CheckRoleAndNameAsync(set, null, name, request, db);
        if (error != null) return error;

        var now = DateTime.UtcNow;
        var item = new T
        {
            Id = Guid.NewGuid(),
            Name = name,
            SortOrder = request.SortOrder,
            IsActive = request.IsActive,
            CreatedAt = now,
            UpdatedAt = now,
        };
        if (item is JobRole role) role.JobDepartmentId = request.JobDepartmentId!.Value;

        set.Add(item);
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { return NameTaken(); }

        return CreatedResponse(StayJobHandlers.ToItem(item), $"/api/v1/admin/stay-jobs/lookups/{kind}/{item.Id}");
    }

    private static async Task<IResult> UpdateAsync<T>(
        DbSet<T> set, Guid id, UpsertJobLookupRequest request, ApplicationDbContext db)
        where T : JobLookupBase
    {
        var item = await set.FirstOrDefaultAsync(x => x.Id == id);
        if (item == null) return NotFoundResponse(ItemNotFound);

        var name = request.Name.Trim();
        var error = await CheckRoleAndNameAsync(set, id, name, request, db);
        if (error != null) return error;

        if (item is JobRole moved && moved.JobDepartmentId != request.JobDepartmentId
            && await db.StayJobs.AnyAsync(j => j.JobRoleId == id))
            return ConflictResponse("In use. Turn it off instead.", StayJobErrorTypes.JobLookupInUse);

        item.Name = name;
        item.SortOrder = request.SortOrder;
        item.IsActive = request.IsActive;
        item.UpdatedAt = DateTime.UtcNow;
        if (item is JobRole role) role.JobDepartmentId = request.JobDepartmentId!.Value;

        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { return NameTaken(); }

        return OkResponse(StayJobHandlers.ToItem(item));
    }

    private static async Task<IResult> DeleteAsync<T>(DbSet<T> set, Guid id, bool inUse, ApplicationDbContext db)
        where T : JobLookupBase
    {
        var item = await set.FirstOrDefaultAsync(x => x.Id == id);
        if (item == null) return NotFoundResponse(ItemNotFound);
        if (inUse) return ConflictResponse("In use. Turn it off instead.", StayJobErrorTypes.JobLookupInUse);
        if (DataSeeder.IsSeededJobLookup(id))
            return ConflictResponse("Built-in. Turn it off instead.", StayJobErrorTypes.JobLookupInUse);

        set.Remove(item);
        await db.SaveChangesAsync();

        return NoContentResponse();
    }
}
