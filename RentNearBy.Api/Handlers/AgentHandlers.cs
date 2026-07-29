using System.Security.Claims;
using FluentValidation;
using Mapster;
using Microsoft.AspNetCore.Http;
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

// Agent CRUD + photo + service bulk-set (admin-managed) + the consumer app's own "am I an agent"
// check (GetMyAgentProfile) — an Agent is a role on an existing User account, not a separate
// identity, so that one method is the only place this class is called from the consumer app itself.
public static class AgentHandlers
{
    private const long MaxImageBytes = 10 * 1024 * 1024;

    // serviceId provided -> service-scoped, active-only picker (admin's enquiry-assign flow).
    // Omitted -> full admin list (all statuses), matches the flat-route optional-query-param convention.
    public static async Task<IResult> GetAgents(Guid? serviceId, IUnitOfWork unitOfWork)
    {
        var agents = serviceId.HasValue
            ? await unitOfWork.Agents.GetActiveByServiceIdAsync(serviceId.Value)
            : await unitOfWork.Agents.GetAllWithServicesAsync();
        return OkResponse(agents.Select(a => a.Adapt<AgentDto>()));
    }

    public static async Task<IResult> GetAgentById(Guid id, IUnitOfWork unitOfWork)
    {
        var agent = await unitOfWork.Agents.GetByIdWithServicesAsync(id);
        if (agent == null) return NotFoundResponse("Agent not found");
        return OkResponse(agent.Adapt<AgentDto>());
    }

    // ── Consumer-facing ──────────────────────────────────────────────────────
    // A 404 here is the expected case for ~all users, not an error — never treat it as one client-side.
    public static async Task<IResult> GetMyAgentProfile(ClaimsPrincipal principal, IUnitOfWork unitOfWork)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var agent = await unitOfWork.Agents.GetByUserIdAsync(userId);
        if (agent == null) return NotFoundResponse("Not an agent");

        var pendingCount = await unitOfWork.Enquiries.GetUnseenCountForAgentAsync(agent.Id);
        return OkResponse(new MyAgentProfileDto { AgentId = agent.Id, Name = agent.Name, PendingLeadCount = pendingCount });
    }

    // ── Lead stats (month-wise, one year at a time) ─────────────────────────
    // One shared assembly between the agent's own Dashboard and the admin's Agent Stats page — both
    // read from the same GroupBy-then-project query (GetMonthlyStatusCountsForAgentAsync), so there's
    // no separate aggregation logic to keep in sync between the two audiences.

    private static AgentLeadStatsDto BuildLeadStatsDto(Guid agentId, string agentName, int year, List<MonthlyStatusCountRow> rows)
    {
        var months = Enumerable.Range(1, 12).Select(m => new MonthlyLeadStatDto { Month = m }).ToList();
        foreach (var row in rows)
        {
            var bucket = months[row.Month - 1];
            if (row.Status == EnquiryStatuses.Submitted) bucket.Submitted = row.Count;
            else if (row.Status == EnquiryStatuses.Contacted) bucket.Contacted = row.Count;
            else if (row.Status == EnquiryStatuses.Closed) bucket.Closed = row.Count;
            bucket.Total += row.Count;
        }

        return new AgentLeadStatsDto
        {
            AgentId = agentId,
            AgentName = agentName,
            Year = year,
            TotalLeads = months.Sum(m => m.Total),
            TotalSubmitted = months.Sum(m => m.Submitted),
            TotalContacted = months.Sum(m => m.Contacted),
            TotalClosed = months.Sum(m => m.Closed),
            Months = months,
        };
    }

    // Agent-facing — resolves identity from JWT, same pattern as GetMyAgentProfile above.
    public static async Task<IResult> GetMyLeadStats(ClaimsPrincipal principal, IUnitOfWork unitOfWork, int? year = null)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var agent = await unitOfWork.Agents.GetByUserIdAsync(userId);
        if (agent == null) return NotFoundResponse("Not an agent");

        var targetYear = year ?? DateTime.UtcNow.Year;
        var rows = await unitOfWork.Enquiries.GetMonthlyStatusCountsForAgentAsync(agent.Id, targetYear);
        return OkResponse(BuildLeadStatsDto(agent.Id, agent.Name, targetYear, rows));
    }

    // Admin-facing — explicit agentId route param, mirrors GetAgentById's shape.
    public static async Task<IResult> GetAdminAgentLeadStats(Guid id, IUnitOfWork unitOfWork, int? year = null)
    {
        var agent = await unitOfWork.Agents.GetByIdAsync(id);
        if (agent == null) return NotFoundResponse("Agent not found");

        var targetYear = year ?? DateTime.UtcNow.Year;
        var rows = await unitOfWork.Enquiries.GetMonthlyStatusCountsForAgentAsync(agent.Id, targetYear);
        return OkResponse(BuildLeadStatsDto(agent.Id, agent.Name, targetYear, rows));
    }

    public static async Task<IResult> AdminCreateAgent(
        CreateAgentRequest request, IValidator<CreateAgentRequest> validator, IUnitOfWork unitOfWork)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var user = await unitOfWork.Users.GetByIdAsync(request.UserId);
        if (user == null) return NotFoundResponse("User not found");

        // Belt-and-suspenders alongside the DB partial-unique index (ix_agents_userid_unique) — this
        // gives a friendly ConflictResponse instead of the request failing on a raw constraint
        // violation at SaveChangesAsync.
        if (await unitOfWork.Agents.ExistsByUserIdAsync(request.UserId))
            return ConflictResponse("This user is already linked to another agent");

        var agent = new Agent
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            Phone = request.Phone.Trim(),
            WhatsAppNumber = request.WhatsAppNumber.Trim(),
            PhotoUrl = string.Empty,
            PhotoFilePath = string.Empty,
            IsActive = true,
            UserId = request.UserId,
            CreatedAt = DateTime.UtcNow,
            Experience = request.Experience,
            CompanyName = request.CompanyName?.Trim(),
        };

        await unitOfWork.Agents.AddAsync(agent);
        await unitOfWork.SaveChangesAsync();

        var created = await unitOfWork.Agents.GetByIdWithServicesAsync(agent.Id);
        return CreatedResponse(created!.Adapt<AgentDto>(), $"/api/v1/agents/{agent.Id}");
    }

    public static async Task<IResult> AdminUpdateAgent(
        Guid id, UpdateAgentRequest request, IValidator<UpdateAgentRequest> validator, IUnitOfWork unitOfWork)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var agent = await unitOfWork.Agents.GetByIdAsync(id);
        if (agent == null) return NotFoundResponse("Agent not found");

        if (request.Name != null) agent.Name = request.Name.Trim();
        if (request.Phone != null) agent.Phone = request.Phone.Trim();
        if (request.WhatsAppNumber != null) agent.WhatsAppNumber = request.WhatsAppNumber.Trim();
        if (request.IsActive.HasValue) agent.IsActive = request.IsActive.Value;
        if (request.Experience.HasValue) agent.Experience = request.Experience.Value;
        if (request.CompanyName != null) agent.CompanyName = request.CompanyName.Trim();

        await unitOfWork.SaveChangesAsync();

        var updated = await unitOfWork.Agents.GetByIdWithServicesAsync(id);
        return OkResponse(updated!.Adapt<AgentDto>());
    }

    public static async Task<IResult> AdminDeleteAgent(
        Guid id, IUnitOfWork unitOfWork, IPhotoService photoService)
    {
        var agent = await unitOfWork.Agents.GetByIdAsync(id);
        if (agent == null) return NotFoundResponse("Agent not found");

        // EnquiryAgent's FK is Cascade (unlike Service/Package's Restrict), so the raw DB delete
        // would succeed silently, quietly dropping the agent's assignment rows — this is a pure
        // business-level guard restricted to "live" enquiries, matching
        // IEnquiryRepository.ExistsByAssignedAgentIdAsync.
        if (await unitOfWork.Enquiries.ExistsByAssignedAgentIdAsync(id))
            return ConflictResponse("Cannot delete an agent currently assigned to a live enquiry. Reassign or resolve those enquiries first.");

        if (!string.IsNullOrEmpty(agent.PhotoFilePath))
            await photoService.DeletePhotoAsync(agent.PhotoFilePath);

        await unitOfWork.Agents.DeleteAsync(agent);
        await unitOfWork.SaveChangesAsync();
        return NoContentResponse();
    }

    public static async Task<IResult> AdminUploadAgentPhoto(
        Guid id, IFormFile image, IUnitOfWork unitOfWork, IPhotoService photoService)
    {
        var agent = await unitOfWork.Agents.GetByIdAsync(id);
        if (agent == null) return NotFoundResponse("Agent not found");
        if (image.Length > MaxImageBytes) return BadRequestResponse("Image size must not exceed 10MB");

        if (!string.IsNullOrEmpty(agent.PhotoFilePath))
            await photoService.DeletePhotoAsync(agent.PhotoFilePath);

        using var stream = image.OpenReadStream();
        var (url, filePath) = await photoService.SaveAgentPhotoAsync(stream, image.FileName, id);

        agent.PhotoUrl = url;
        agent.PhotoFilePath = filePath;
        await unitOfWork.SaveChangesAsync();

        return OkResponse(new { photoUrl = url });
    }

    public static async Task<IResult> AdminDeleteAgentPhoto(
        Guid id, IUnitOfWork unitOfWork, IPhotoService photoService)
    {
        var agent = await unitOfWork.Agents.GetByIdAsync(id);
        if (agent == null) return NotFoundResponse("Agent not found");
        if (string.IsNullOrEmpty(agent.PhotoFilePath)) return BadRequestResponse("No photo to delete");

        await photoService.DeletePhotoAsync(agent.PhotoFilePath);
        agent.PhotoUrl = string.Empty;
        agent.PhotoFilePath = string.Empty;
        await unitOfWork.SaveChangesAsync();

        return NoContentResponse();
    }

    public static async Task<IResult> AdminSetAgentServices(
        Guid id, SetAgentServicesRequest request, IValidator<SetAgentServicesRequest> validator,
        IUnitOfWork unitOfWork, ApplicationDbContext db)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var agent = await unitOfWork.Agents.GetByIdAsync(id);
        if (agent == null) return NotFoundResponse("Agent not found");

        var distinctIds = request.ServiceIds.Distinct().ToList();
        if (distinctIds.Count > 0)
        {
            var validCount = await db.Services.CountAsync(s => distinctIds.Contains(s.Id));
            if (validCount != distinctIds.Count)
                return BadRequestResponse("One or more ServiceIds are invalid");
        }

        // Full-replace, exact mirror of how BannerHandlers manipulates db.BannerDismissals directly.
        var existing = db.AgentServices.Where(as_ => as_.AgentId == id);
        db.AgentServices.RemoveRange(existing);
        db.AgentServices.AddRange(distinctIds.Select(serviceId => new AgentService
        {
            AgentId = id,
            ServiceId = serviceId,
        }));
        await db.SaveChangesAsync();

        var updated = await unitOfWork.Agents.GetByIdWithServicesAsync(id);
        return OkResponse(updated!.Adapt<AgentDto>());
    }
}
