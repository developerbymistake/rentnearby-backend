using System.Security.Claims;
using System.Text.Json;
using FluentValidation;
using Mapster;
using Microsoft.AspNetCore.SignalR;
using RentNearBy.Api.Hubs;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

// Consumer inquiry/lead flow (create/list/detail with ownership check) + admin pipeline management
// (paged/filtered list, detail, status update, agent assign — including the single-combined-write
// Submitted -> Contacted auto-transition on agent assignment).
public static class InquiryHandlers
{
    // ── Consumer-facing ──────────────────────────────────────────────────────

    public static async Task<IResult> CreateInquiry(
        CreateInquiryRequest request, IValidator<CreateInquiryRequest> validator,
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var service = await unitOfWork.Services.GetByIdAsync(request.ServiceId);
        if (service == null) return NotFoundResponse("Service not found");
        if (!service.IsActive) return BadRequestResponse("This service is not currently available");

        var package = await unitOfWork.ServicePackages.GetByIdAsync(request.ServicePackageId);
        if (package == null) return NotFoundResponse("Package not found");
        if (package.ServiceId != request.ServiceId)
            return BadRequestResponse("This package does not belong to the specified service");
        if (!package.IsActive) return BadRequestResponse("This package is not currently available");

        var now = DateTime.UtcNow;
        var inquiry = new Inquiry
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ServiceId = request.ServiceId,
            ServicePackageId = request.ServicePackageId,
            FullName = request.FullName.Trim(),
            Mobile = request.Mobile.Trim(),
            Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim(),
            // PreferredDateOrTripStart is `timestamp with time zone` — Npgsql throws when writing a
            // Kind=Unspecified DateTime into that column type. A client that serializes without a
            // 'Z'/offset (as this app's Flutter client used to) deserializes to Unspecified here, so
            // this normalizes at the system boundary regardless of what any client sends.
            PreferredDateOrTripStart = request.PreferredDateOrTripStart.HasValue
                ? DateTime.SpecifyKind(request.PreferredDateOrTripStart.Value, DateTimeKind.Utc)
                : null,
            NumberOfPeople = request.NumberOfPeople,
            Message = string.IsNullOrWhiteSpace(request.Message) ? null : request.Message.Trim(),
            Status = InquiryStatuses.Submitted,
            AssignedAgentId = null,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await unitOfWork.Inquiries.AddAsync(inquiry);

        // Initial ledger entry so the status timeline always starts from a real "Submitted" event,
        // append-only ledger mirrors CoinTransaction's shape — never a gap between row creation and
        // its first history entry.
        db.InquiryStatusHistories.Add(new InquiryStatusHistory
        {
            Id = Guid.NewGuid(),
            InquiryId = inquiry.Id,
            Status = InquiryStatuses.Submitted,
            ChangedByAdminId = null,
            Note = null,
            CreatedAt = now,
        });

        await unitOfWork.SaveChangesAsync();

        var detail = await unitOfWork.Inquiries.GetByIdWithDetailsAsync(inquiry.Id);
        return CreatedResponse(detail!.Adapt<InquiryDetailDto>(), $"/api/v1/inquiries/{inquiry.Id}");
    }

    public static async Task<IResult> GetMyInquiries(ClaimsPrincipal principal, IUnitOfWork unitOfWork)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var inquiries = await unitOfWork.Inquiries.GetByUserIdAsync(userId);
        return OkResponse(inquiries.Select(i => i.Adapt<InquiryListItemDto>()));
    }

    public static async Task<IResult> GetInquiryDetail(Guid id, ClaimsPrincipal principal, IUnitOfWork unitOfWork)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var inquiry = await unitOfWork.Inquiries.GetByIdWithDetailsAsync(id);
        if (inquiry == null) return NotFoundResponse("Inquiry not found");
        if (inquiry.UserId != userId) return ForbiddenResponse("You do not own this inquiry");

        return OkResponse(inquiry.Adapt<InquiryDetailDto>());
    }

    // ── Agent-facing (consumer app, scoped to the caller's own linked Agent) ───
    // Every method here resolves the caller's Agent from their own JWT-derived UserId — never from
    // a client-supplied agentId — so an agent can only ever see/act on their own leads.

    public static async Task<IResult> GetMyLeads(
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, int page = 1, int pageSize = 20)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var agent = await unitOfWork.Agents.GetByUserIdAsync(userId);
        if (agent == null) return ForbiddenResponse("You are not an agent");

        if (pageSize < 1 || pageSize > 50) pageSize = 20;
        if (page < 1) page = 1;

        var (items, hasMore) = await unitOfWork.Inquiries.GetByAssignedAgentIdAsync(agent.Id, page, pageSize);
        var dtos = items.Select(i => i.Adapt<InquiryListItemDto>());
        return OkResponse(new { items = dtos, hasMore });
    }

    public static async Task<IResult> GetMyLeadDetail(Guid id, ClaimsPrincipal principal, IUnitOfWork unitOfWork)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var agent = await unitOfWork.Agents.GetByUserIdAsync(userId);
        if (agent == null) return ForbiddenResponse("You are not an agent");

        var inquiry = await unitOfWork.Inquiries.GetByIdWithDetailsAsync(id);
        if (inquiry == null) return NotFoundResponse("Inquiry not found");
        if (inquiry.AssignedAgentId != agent.Id) return ForbiddenResponse("This lead is not assigned to you");

        return OkResponse(inquiry.Adapt<InquiryDetailDto>());
    }

    // Reuses AdminUpdateInquiryStatusRequest/its validator as-is — same shape (Status, Note), same
    // allowed-status rule, no reason to duplicate the type just because a different actor calls it.
    public static async Task<IResult> UpdateMyLeadStatus(
        Guid id, AdminUpdateInquiryStatusRequest request, IValidator<AdminUpdateInquiryStatusRequest> validator,
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db,
        IHubContext<InquiryHub> hubContext, IRabbitMqPublisher publisher)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var agent = await unitOfWork.Agents.GetByUserIdAsync(userId);
        if (agent == null) return ForbiddenResponse("You are not an agent");

        var inquiry = await unitOfWork.Inquiries.GetByIdAsync(id);
        if (inquiry == null) return NotFoundResponse("Inquiry not found");
        if (inquiry.AssignedAgentId != agent.Id) return ForbiddenResponse("This lead is not assigned to you");

        inquiry.Status = request.Status;
        inquiry.UpdatedAt = DateTime.UtcNow;

        db.InquiryStatusHistories.Add(new InquiryStatusHistory
        {
            Id = Guid.NewGuid(),
            InquiryId = inquiry.Id,
            Status = request.Status,
            ChangedByAgentId = agent.Id,
            Note = request.Note,
            CreatedAt = DateTime.UtcNow,
        });

        await unitOfWork.SaveChangesAsync();

        var updated = await unitOfWork.Inquiries.GetByIdWithDetailsAsync(id);

        await PushInquiryStatusChangedAsync(hubContext, inquiry.UserId, id, inquiry.Status, agentAssigned: true);
        await PublishInquiryStatusPushAsync(publisher, inquiry.UserId, id, updated!.Service.Name, inquiry.Status);

        return OkResponse(updated!.Adapt<InquiryDetailDto>());
    }

    // ── Admin-facing ─────────────────────────────────────────────────────────

    public static async Task<IResult> AdminGetInquiries(
        IUnitOfWork unitOfWork, string? status = null, Guid? serviceSectionId = null, int page = 1, int pageSize = 20)
    {
        if (pageSize < 1 || pageSize > 50) pageSize = 20;
        if (page < 1) page = 1;

        var (items, hasMore) = await unitOfWork.Inquiries.GetAdminFilteredPagedAsync(status, serviceSectionId, page, pageSize);
        var dtos = items.Select(i => i.Adapt<InquiryListItemDto>());
        return OkResponse(new { items = dtos, hasMore });
    }

    public static async Task<IResult> AdminGetInquiryDetail(Guid id, IUnitOfWork unitOfWork)
    {
        var inquiry = await unitOfWork.Inquiries.GetByIdWithDetailsAsync(id);
        if (inquiry == null) return NotFoundResponse("Inquiry not found");
        return OkResponse(inquiry.Adapt<InquiryDetailDto>());
    }

    public static async Task<IResult> AdminUpdateInquiryStatus(
        Guid id, AdminUpdateInquiryStatusRequest request, IValidator<AdminUpdateInquiryStatusRequest> validator,
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db,
        IHubContext<InquiryHub> hubContext, IRabbitMqPublisher publisher)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        if (!AdminAuthHandlers.TryGetAdminId(principal, out var adminId))
            return UnauthorizedResponse();

        var inquiry = await unitOfWork.Inquiries.GetByIdAsync(id);
        if (inquiry == null) return NotFoundResponse("Inquiry not found");

        inquiry.Status = request.Status;
        inquiry.UpdatedAt = DateTime.UtcNow;

        db.InquiryStatusHistories.Add(new InquiryStatusHistory
        {
            Id = Guid.NewGuid(),
            InquiryId = inquiry.Id,
            Status = request.Status,
            ChangedByAdminId = adminId,
            Note = request.Note,
            CreatedAt = DateTime.UtcNow,
        });

        await unitOfWork.SaveChangesAsync();

        var updated = await unitOfWork.Inquiries.GetByIdWithDetailsAsync(id);

        // Both fire here, after the save: SignalR live-update for an open app, RabbitMQ-queued FCM
        // for a backgrounded/killed one (mirrors chat's dual pattern, not wallet's SignalR-only one).
        await PushInquiryStatusChangedAsync(hubContext, inquiry.UserId, id, inquiry.Status, agentAssigned: inquiry.AssignedAgentId.HasValue);
        await PublishInquiryStatusPushAsync(publisher, inquiry.UserId, id, updated!.Service.Name, inquiry.Status);

        return OkResponse(updated!.Adapt<InquiryDetailDto>());
    }

    public static async Task<IResult> AdminAssignAgent(
        Guid id, AdminAssignAgentRequest request, IValidator<AdminAssignAgentRequest> validator,
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db,
        IHubContext<InquiryHub> hubContext, IRabbitMqPublisher publisher)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        if (!AdminAuthHandlers.TryGetAdminId(principal, out var adminId))
            return UnauthorizedResponse();

        var inquiry = await unitOfWork.Inquiries.GetByIdAsync(id);
        if (inquiry == null) return NotFoundResponse("Inquiry not found");

        if (request.AgentId.HasValue)
        {
            var agent = await unitOfWork.Agents.GetByIdAsync(request.AgentId.Value);
            if (agent == null) return NotFoundResponse("Agent not found");
        }

        inquiry.AssignedAgentId = request.AgentId;
        inquiry.UpdatedAt = DateTime.UtcNow;

        // Single-combined-write auto-transition: assigning an agent while the Inquiry is Submitted
        // flips it to Contacted as ONE InquiryStatusHistory row in the SAME SaveChangesAsync call as
        // the agent assignment — never two independent admin actions/writes. A plain reassignment or
        // unassignment (status not Submitted, or AgentId null) writes no history row at all, since
        // the agent field itself isn't part of this status ledger.
        var autoTransitioned = request.AgentId.HasValue && inquiry.Status == InquiryStatuses.Submitted;
        if (autoTransitioned)
        {
            inquiry.Status = InquiryStatuses.Contacted;
            db.InquiryStatusHistories.Add(new InquiryStatusHistory
            {
                Id = Guid.NewGuid(),
                InquiryId = inquiry.Id,
                Status = InquiryStatuses.Contacted,
                ChangedByAdminId = adminId,
                Note = "Auto-moved to Contacted on agent assignment",
                CreatedAt = DateTime.UtcNow,
            });
        }

        await unitOfWork.SaveChangesAsync();

        var updated = await unitOfWork.Inquiries.GetByIdWithDetailsAsync(id);

        // Fires only when autoTransitioned, mirroring AdminUpdateInquiryStatus's push — a pure
        // reassignment/unassignment with no status change pushes nothing.
        if (autoTransitioned)
        {
            await PushInquiryStatusChangedAsync(hubContext, inquiry.UserId, id, inquiry.Status, agentAssigned: true);
            await PublishInquiryStatusPushAsync(publisher, inquiry.UserId, id, updated!.Service.Name, inquiry.Status);
        }

        return OkResponse(updated!.Adapt<InquiryDetailDto>());
    }

    // ── Push helpers ─────────────────────────────────────────────────────────

    // Best-effort — a SignalR push failure must never turn an already-committed status update into
    // an error response. Only called from a point where the caller's own commit is already final.
    // Clone of GoLiveHandlers.PushWalletBalanceChangedAsync's shape.
    private static async Task PushInquiryStatusChangedAsync(
        IHubContext<InquiryHub> hubContext, Guid userId, Guid inquiryId, string status, bool agentAssigned)
    {
        try
        {
            await hubContext.Clients.Group($"user_{userId}").SendAsync("InquiryStatusChanged", new
            {
                inquiryId,
                status,
                agentAssigned,
            });
        }
        catch { }
    }

    // Fire-and-forget by design: a slow/failed publish here must never block the admin's response.
    // The consumer worker resolves device tokens and sends the actual push independently (see
    // InquiryStatusPushWorkerService). Clone of ChatHandlers.PublishPushNotificationAsync's shape.
    private static async Task PublishInquiryStatusPushAsync(
        IRabbitMqPublisher publisher, Guid userId, Guid inquiryId, string serviceName, string status)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new InquiryStatusPushPayload(userId, inquiryId, serviceName, status));
            await publisher.PublishAsync("inquiry.status.push", payload);
        }
        catch
        {
            // Never let a push-notification publish failure fail the status update itself.
        }
    }
}
