using System.Security.Claims;
using System.Text.Json;
using FluentValidation;
using Mapster;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
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

// Consumer enquiry/lead flow (create/list/detail with ownership check) + admin pipeline management
// (paged/filtered list, detail, status update, agent assign — including the single-combined-write
// Submitted -> Contacted auto-transition on agent assignment).
public static class EnquiryHandlers
{
    // ── Consumer-facing ──────────────────────────────────────────────────────

    // internal (not private) — WebEnquiryHandlers.VerifyOtp reuses these exact same keys/limits so a
    // website-originated enquiry is bound by the identical per-user/per-target-mobile caps as the app.
    internal static readonly TimeSpan EnquiryCreateWindow = TimeSpan.FromHours(24);
    internal const int EnquiryCreatePerUserMax = 10;
    // Tighter than the per-user cap and keyed on the TARGET number, not the caller — this is the one
    // that actually bounds real-world harm from the "submit for someone else" override (see
    // EnquiryContactSheet): the contact number is never verified, and it's exactly what category-
    // mapped agents place real outbound calls/WhatsApp messages to. A per-user cap alone doesn't stop
    // several attacker accounts (or the same one across days) from each independently targeting the
    // same real third-party phone number.
    internal const int EnquiryCreatePerMobileMax = 3;

    public static async Task<IResult> CreateEnquiry(
        CreateEnquiryRequest request, IValidator<CreateEnquiryRequest> validator,
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db,
        IHubContext<EnquiryHub> hubContext, IRabbitMqPublisher publisher,
        IRateLimitService rateLimiter, HttpContext httpContext)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var userRl = await rateLimiter.CheckAsync($"enquiry:create:{userId}", EnquiryCreatePerUserMax, EnquiryCreateWindow);
        if (!userRl.IsAllowed)
        {
            httpContext.Response.Headers["Retry-After"] = ((int)userRl.RetryAfter!.Value.TotalSeconds).ToString();
            return TooManyRequestsResponse();
        }
        var mobileRl = await rateLimiter.CheckAsync($"enquiry:create:mobile:{request.Mobile.Trim()}", EnquiryCreatePerMobileMax, EnquiryCreateWindow);
        if (!mobileRl.IsAllowed)
        {
            httpContext.Response.Headers["Retry-After"] = ((int)mobileRl.RetryAfter!.Value.TotalSeconds).ToString();
            return TooManyRequestsResponse();
        }

        return await CreateEnquiryCore(userId, request, unitOfWork, db, hubContext, publisher);
    }

    // Extracted so WebEnquiryHandlers.VerifyOtp (no ClaimsPrincipal — userId comes from a phone-verified/
    // just-created User instead) can reuse the exact same enquiry-creation + auto-assign + notify logic
    // without duplicating it. Pure extraction — CreateEnquiry's own behavior above is unchanged, it now
    // just calls this instead of inlining it. The caller is responsible for its own rate-limiting (the
    // public CreateEnquiry above does its checks before calling in; WebEnquiryHandlers does its own,
    // identical checks — see EnquiryCreatePerUserMax/EnquiryCreatePerMobileMax reuse there).
    internal static async Task<IResult> CreateEnquiryCore(
        Guid userId, CreateEnquiryRequest request, IUnitOfWork unitOfWork, ApplicationDbContext db,
        IHubContext<EnquiryHub> hubContext, IRabbitMqPublisher publisher)
    {
        var service = await unitOfWork.Services.GetByIdAsync(request.ServiceId);
        if (service == null) return NotFoundResponse("Service not found");
        if (!service.IsActive) return BadRequestResponse("This service is not currently available");

        var package = await unitOfWork.ServicePackages.GetByIdAsync(request.ServicePackageId);
        if (package == null) return NotFoundResponse("Package not found");
        if (package.ServiceId != request.ServiceId)
            return BadRequestResponse("This package does not belong to the specified service");
        if (!package.IsActive) return BadRequestResponse("This package is not currently available");

        var now = DateTime.UtcNow;
        var enquiry = new Enquiry
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
            Status = EnquiryStatuses.Submitted,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await unitOfWork.Enquiries.AddAsync(enquiry);

        // Initial ledger entry so the status timeline always starts from a real "Submitted" event,
        // append-only ledger mirrors CreditTransaction's shape — never a gap between row creation and
        // its first history entry.
        db.EnquiryStatusHistories.Add(new EnquiryStatusHistory
        {
            Id = Guid.NewGuid(),
            EnquiryId = enquiry.Id,
            Status = EnquiryStatuses.Submitted,
            ChangedByAdminId = null,
            Note = null,
            CreatedAt = now,
        });

        // Auto-assign: every currently-active Agent mapped to this exact Service picks up the
        // lead immediately, with zero Admin action — reuses the exact assign -> auto-transition ->
        // notify shapes AdminSetEnquiryAgents uses for a manual assignment. If no agent is mapped to
        // the service, the enquiry stays Submitted/unassigned exactly as before this feature.
        // Excludes the submitter themselves if they happen to also be a mapped Agent (an Agent is
        // just a role on an existing consumer User account, not a separate identity) — booking a
        // service for themselves must never leave them self-assigned as their own lead's agent. If
        // that leaves zero agents, this falls through to the same "unassigned" path as no mapped
        // agent at all — mirrors the identical guard in AdminSetEnquiryAgents below.
        var serviceAgents = (await unitOfWork.Agents.GetActiveByServiceIdAsync(service.Id))
            .Where(a => a.UserId != userId)
            .ToList();
        var autoTransitioned = serviceAgents.Count > 0;
        var notificationsToSend = new List<NotificationEvent>();
        if (autoTransitioned)
        {
            foreach (var agent in serviceAgents)
            {
                db.EnquiryAgents.Add(new EnquiryAgent { EnquiryId = enquiry.Id, AgentId = agent.Id, AssignedAt = now });
                if (agent.UserId.HasValue)
                {
                    var notification = BuildLeadAssignedNotification(agent, enquiry);
                    db.NotificationEvents.Add(notification);
                    notificationsToSend.Add(notification);
                }
            }

            // Bypasses EnquiryStatusTransitions.IsAllowed — safe only because the enquiry was just
            // created as Submitted (line ~66) and Submitted -> Contacted is unconditionally legal in
            // the table. If that transition is ever removed, this direct assignment must be guarded too.
            enquiry.Status = EnquiryStatuses.Contacted;
            enquiry.UpdatedAt = now;
            db.EnquiryStatusHistories.Add(new EnquiryStatusHistory
            {
                Id = Guid.NewGuid(),
                EnquiryId = enquiry.Id,
                Status = EnquiryStatuses.Contacted,
                Note = "Auto-assigned to service-mapped agent(s) on submission",
                CreatedAt = now,
            });
        }

        await unitOfWork.SaveChangesAsync();

        var detail = await unitOfWork.Enquiries.GetByIdWithDetailsAsync(enquiry.Id);

        if (autoTransitioned)
        {
            await PushEnquiryStatusChangedAsync(hubContext, enquiry.UserId, enquiry.Id, enquiry.Status, agentAssigned: true);
            await PublishEnquiryStatusPushAsync(publisher, enquiry.UserId, enquiry.Id, detail!.Service.Name, enquiry.Status);
        }
        else
        {
            // No agent picked up this lead at all (no active agent mapped to the category, or the
            // only mapped one was the submitter themselves) — nobody would otherwise know this
            // enquiry exists to act on it. Best-effort, mirrors EscalateEnquiry's publish shape.
            try
            {
                var message = new EnquiryUnassignedMessage
                {
                    EnquiryId = enquiry.Id,
                    ServiceName = service.Name,
                    ConsumerName = enquiry.FullName,
                };
                await publisher.PublishAsync("enquiry.unassigned", JsonSerializer.Serialize(message));
            }
            catch { }
        }
        await SendNotificationEventsAsync(hubContext, publisher, notificationsToSend);

        return CreatedResponse(detail!.Adapt<EnquiryDetailDto>(), $"/api/v1/enquiries/{enquiry.Id}");
    }

    public static async Task<IResult> GetMyEnquiries(ClaimsPrincipal principal, IUnitOfWork unitOfWork)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var enquiries = await unitOfWork.Enquiries.GetByUserIdAsync(userId);
        return OkResponse(enquiries.Select(i => i.Adapt<EnquiryListItemDto>()));
    }

    // Server-anchored counterpart to GetMyEnquiries, for the Explore tab's Enquiries badge — same
    // `{ count }` shape as ChatHandlers.GetUnreadCount so the client parses every count endpoint
    // identically. Exists so the badge stays correct without the client having to keep a full
    // myEnquiries list loaded/fresh just to derive a count from it. Counts unseen (not-Closed AND
    // updated since the consumer last saw it), not just "active by status" — route path is
    // /enquiries/active-count.
    public static async Task<IResult> GetMyActiveEnquiryCount(ClaimsPrincipal principal, IUnitOfWork unitOfWork)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var count = await unitOfWork.Enquiries.GetUnseenCountForUserAsync(userId);
        return OkResponse(new { count });
    }

    // Marks this enquiry seen by its owning consumer, re-anchoring GetMyActiveEnquiryCount's badge.
    // Always OkResponse — no existence/ownership leak, mirrors MarkNotificationRead's idiom.
    public static async Task<IResult> MarkEnquirySeen(Guid id, ClaimsPrincipal principal, IUnitOfWork unitOfWork)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        await unitOfWork.Enquiries.MarkSeenByUserAsync(id, userId);
        return OkResponse(new { message = "Marked seen" });
    }

    // Marks this lead seen by the caller's own linked Agent, re-anchoring GetUnseenCountForAgentAsync's
    // badge. Always OkResponse when the caller is an agent — no existence/ownership leak.
    public static async Task<IResult> MarkLeadSeen(Guid id, ClaimsPrincipal principal, IUnitOfWork unitOfWork)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var agent = await unitOfWork.Agents.GetByUserIdAsync(userId);
        if (agent == null) return ForbiddenResponse("You are not an agent");

        await unitOfWork.Enquiries.MarkSeenByAgentAsync(id, agent.Id);
        return OkResponse(new { message = "Marked seen" });
    }

    public static async Task<IResult> GetEnquiryDetail(Guid id, ClaimsPrincipal principal, IUnitOfWork unitOfWork)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var enquiry = await unitOfWork.Enquiries.GetByIdWithDetailsAsync(id);
        if (enquiry == null) return NotFoundResponse("Enquiry not found");
        if (enquiry.UserId != userId) return ForbiddenResponse("You do not own this enquiry");

        return OkResponse(enquiry.Adapt<EnquiryDetailDto>());
    }

    // Self-service "report an issue with my agent" — never seen by the agent(s) themselves, surfaces
    // to Admin only via the Enquiries list flag/filter and detail card. Blocked with no agent
    // assigned yet, and while a Pending escalation already exists (DB-enforced, not just this check —
    // see the partial unique index in ApplicationDbContext.cs).
    public static async Task<IResult> EscalateEnquiry(
        Guid id, EscalateEnquiryRequest request, IValidator<EscalateEnquiryRequest> validator,
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db, IRabbitMqPublisher publisher)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var enquiry = await unitOfWork.Enquiries.GetByIdAsync(id);
        if (enquiry == null) return NotFoundResponse("Enquiry not found");
        if (enquiry.UserId != userId) return ForbiddenResponse("You do not own this enquiry");

        if (!await db.EnquiryAgents.AnyAsync(ia => ia.EnquiryId == id))
            return BadRequestResponse("No agent is assigned to this enquiry yet");

        db.EnquiryEscalations.Add(new EnquiryEscalation
        {
            Id = Guid.NewGuid(),
            EnquiryId = id,
            Reason = request.Reason,
            Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
            Status = "Pending",
            CreatedAt = DateTime.UtcNow,
        });

        try
        {
            await unitOfWork.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            return ConflictResponse("You already have an open report for this enquiry");
        }

        var updated = await unitOfWork.Enquiries.GetByIdWithDetailsAsync(id);

        // Best-effort — mirrors ListingsHandlers/PlotHandlers' report.filed publish shape exactly. A
        // publish failure must never turn an already-saved escalation into an error response.
        try
        {
            var agentNames = string.Join(", ", updated!.EnquiryAgents.Select(ia => ia.Agent.Name));
            var message = new EscalationFiledMessage
            {
                EnquiryId = id,
                ConsumerName = updated.FullName,
                AgentName = string.IsNullOrEmpty(agentNames) ? "the assigned agent" : agentNames,
                Reason = request.Reason,
            };
            await publisher.PublishAsync("escalation.filed", JsonSerializer.Serialize(message));
        }
        catch { }

        return OkResponse(updated!.Adapt<EnquiryDetailDto>());
    }

    // ── Agent-facing (consumer app, scoped to the caller's own linked Agent) ───
    // Every method here resolves the caller's Agent from their own JWT-derived UserId — never from
    // a client-supplied agentId — so an agent can only ever see/act on their own leads.
    //
    // Every agent-facing response is also run through StripEscalationDataForAgent — an Enquiry's
    // escalation(s) are a consumer's complaint that may be ABOUT this exact agent, and are by design
    // "never seen by the assigned agent(s), only the reporting consumer and Admin" (see
    // EnquiryDetailDto's own field comment). Adapt<EnquiryDetailDto>()/Adapt<EnquiryListItemDto>()
    // otherwise carry the full escalation data through unchanged, since the same Mapster config is
    // shared with the consumer/admin call sites where it's meant to be present.

    private static EnquiryDetailDto StripEscalationDataForAgent(EnquiryDetailDto dto)
    {
        dto.Escalations = [];
        return dto;
    }

    private static EnquiryListItemDto StripEscalationDataForAgent(EnquiryListItemDto dto)
    {
        dto.HasPendingEscalation = false;
        return dto;
    }

    public static async Task<IResult> GetMyLeads(
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, int page = 1, int pageSize = 20)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var agent = await unitOfWork.Agents.GetByUserIdAsync(userId);
        if (agent == null) return ForbiddenResponse("You are not an agent");

        if (pageSize < 1 || pageSize > 50) pageSize = 20;
        if (page < 1) page = 1;

        var (items, hasMore) = await unitOfWork.Enquiries.GetByAssignedAgentIdAsync(agent.Id, page, pageSize);
        var dtos = items.Select(i => StripEscalationDataForAgent(i.Adapt<EnquiryListItemDto>()));
        return OkResponse(new { items = dtos, hasMore });
    }

    public static async Task<IResult> GetMyLeadDetail(Guid id, ClaimsPrincipal principal, IUnitOfWork unitOfWork)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var agent = await unitOfWork.Agents.GetByUserIdAsync(userId);
        if (agent == null) return ForbiddenResponse("You are not an agent");

        var enquiry = await unitOfWork.Enquiries.GetByIdWithDetailsAsync(id);
        if (enquiry == null) return NotFoundResponse("Enquiry not found");
        if (!enquiry.EnquiryAgents.Any(ia => ia.AgentId == agent.Id)) return ForbiddenResponse("This lead is not assigned to you");

        return OkResponse(StripEscalationDataForAgent(enquiry.Adapt<EnquiryDetailDto>()));
    }

    // Reuses AdminUpdateEnquiryStatusRequest/its validator as-is — same shape (Status, Note), same
    // allowed-status rule, no reason to duplicate the type just because a different actor calls it.
    public static async Task<IResult> UpdateMyLeadStatus(
        Guid id, AdminUpdateEnquiryStatusRequest request, IValidator<AdminUpdateEnquiryStatusRequest> validator,
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db,
        IHubContext<EnquiryHub> hubContext, IRabbitMqPublisher publisher)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var agent = await unitOfWork.Agents.GetByUserIdAsync(userId);
        if (agent == null) return ForbiddenResponse("You are not an agent");

        var enquiry = await unitOfWork.Enquiries.GetByIdAsync(id);
        if (enquiry == null) return NotFoundResponse("Enquiry not found");
        if (!await unitOfWork.Enquiries.IsAgentAssignedAsync(id, agent.Id)) return ForbiddenResponse("This lead is not assigned to you");

        if (!EnquiryStatusTransitions.IsAllowed(enquiry.Status, request.Status))
            return BadRequestResponse($"Cannot change status from {enquiry.Status} to {request.Status}.");

        enquiry.Status = request.Status;
        enquiry.UpdatedAt = DateTime.UtcNow;

        db.EnquiryStatusHistories.Add(new EnquiryStatusHistory
        {
            Id = Guid.NewGuid(),
            EnquiryId = enquiry.Id,
            Status = request.Status,
            ChangedByAgentId = agent.Id,
            Note = request.Note,
            CreatedAt = DateTime.UtcNow,
        });

        try
        {
            await unitOfWork.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another agent co-assigned to this same lead, or Admin, committed a change to this exact
            // Enquiry row in the moment between this handler's read and write — never silently clobber it.
            return ConflictResponse("This lead was just updated by another request. Please retry.", "CONCURRENT_UPDATE");
        }

        var updated = await unitOfWork.Enquiries.GetByIdWithDetailsAsync(id);

        await PushEnquiryStatusChangedAsync(hubContext, enquiry.UserId, id, enquiry.Status, agentAssigned: true);
        await PublishEnquiryStatusPushAsync(publisher, enquiry.UserId, id, updated!.Service.Name, enquiry.Status);
        // Every OTHER agent co-assigned to this same lead needs to hear about the change too — not
        // just the consumer. Reuses the exact same push/publish helpers, once per agent's UserId.
        await NotifyCoAssignedAgentsOfStatusChangeAsync(hubContext, publisher, updated!, excludeAgentId: agent.Id);

        // Admin otherwise has no way to know an agent updated a lead's status short of manually
        // checking the dashboard. Best-effort, mirrors EscalateEnquiry's publish shape. Deliberately
        // NOT mirrored in AdminUpdateEnquiryStatus — Admin doesn't need telling about its own action.
        try
        {
            var message = new AgentLeadStatusUpdatedMessage
            {
                EnquiryId = id,
                ServiceName = updated!.Service.Name,
                AgentName = agent.Name,
                Status = request.Status,
            };
            await publisher.PublishAsync("agent.lead.status.updated", JsonSerializer.Serialize(message));
        }
        catch { }

        return OkResponse(StripEscalationDataForAgent(updated!.Adapt<EnquiryDetailDto>()));
    }

    // ── Admin-facing ─────────────────────────────────────────────────────────

    public static async Task<IResult> AdminGetEnquiries(
        IUnitOfWork unitOfWork, string? status = null, Guid? serviceCategoryId = null,
        bool? escalatedOnly = null, int page = 1, int pageSize = 20)
    {
        if (pageSize < 1 || pageSize > 50) pageSize = 20;
        if (page < 1) page = 1;

        var (items, hasMore) = await unitOfWork.Enquiries.GetAdminFilteredPagedAsync(status, serviceCategoryId, escalatedOnly, page, pageSize);
        var dtos = items.Select(i => i.Adapt<EnquiryListItemDto>());
        return OkResponse(new { items = dtos, hasMore });
    }

    public static async Task<IResult> AdminGetEnquiryDetail(Guid id, IUnitOfWork unitOfWork)
    {
        var enquiry = await unitOfWork.Enquiries.GetByIdWithDetailsAsync(id);
        if (enquiry == null) return NotFoundResponse("Enquiry not found");
        return OkResponse(enquiry.Adapt<EnquiryDetailDto>());
    }

    public static async Task<IResult> AdminUpdateEnquiryStatus(
        Guid id, AdminUpdateEnquiryStatusRequest request, IValidator<AdminUpdateEnquiryStatusRequest> validator,
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db,
        IHubContext<EnquiryHub> hubContext, IRabbitMqPublisher publisher)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        if (!AdminAuthHandlers.TryGetAdminId(principal, out var adminId))
            return UnauthorizedResponse();

        var enquiry = await unitOfWork.Enquiries.GetByIdAsync(id);
        if (enquiry == null) return NotFoundResponse("Enquiry not found");

        if (!EnquiryStatusTransitions.IsAllowed(enquiry.Status, request.Status))
            return BadRequestResponse($"Cannot change status from {enquiry.Status} to {request.Status}.");

        enquiry.Status = request.Status;
        enquiry.UpdatedAt = DateTime.UtcNow;

        db.EnquiryStatusHistories.Add(new EnquiryStatusHistory
        {
            Id = Guid.NewGuid(),
            EnquiryId = enquiry.Id,
            Status = request.Status,
            ChangedByAdminId = adminId,
            Note = request.Note,
            CreatedAt = DateTime.UtcNow,
        });

        try
        {
            await unitOfWork.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            // An agent (or a second admin) committed a change to this exact Enquiry row in the moment
            // between this handler's read and write — never silently clobber it.
            return ConflictResponse("This enquiry was just updated by another request. Please retry.", "CONCURRENT_UPDATE");
        }

        var updated = await unitOfWork.Enquiries.GetByIdWithDetailsAsync(id);

        // Both fire here, after the save: SignalR live-update for an open app, RabbitMQ-queued FCM
        // for a backgrounded/killed one (mirrors chat's dual pattern, not wallet's SignalR-only one).
        await PushEnquiryStatusChangedAsync(hubContext, enquiry.UserId, id, enquiry.Status, agentAssigned: updated!.EnquiryAgents.Count > 0);
        await PublishEnquiryStatusPushAsync(publisher, enquiry.UserId, id, updated!.Service.Name, enquiry.Status);
        // Every agent assigned to this lead needs to hear about an admin-driven status change too.
        await NotifyCoAssignedAgentsOfStatusChangeAsync(hubContext, publisher, updated!);

        return OkResponse(updated!.Adapt<EnquiryDetailDto>());
    }

    // Full-set-replace, exact mirror of AgentHandlers.AdminSetAgentServices's delete-all-then
    // -reinsert idiom for the same many-to-many shape — one call replaces the whole assigned-agent
    // set rather than exposing separate add/remove verbs.
    public static async Task<IResult> AdminSetEnquiryAgents(
        Guid id, AdminSetEnquiryAgentsRequest request, IValidator<AdminSetEnquiryAgentsRequest> validator,
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db,
        IHubContext<EnquiryHub> hubContext, IRabbitMqPublisher publisher)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        if (!AdminAuthHandlers.TryGetAdminId(principal, out var adminId))
            return UnauthorizedResponse();

        var enquiry = await unitOfWork.Enquiries.GetByIdAsync(id);
        if (enquiry == null) return NotFoundResponse("Enquiry not found");

        // Hoisted out of the requestedIds.Count > 0 block below — needed unconditionally for the
        // removed-agent/agent-changed notification bodies further down, including when the admin
        // clears the set to empty (requestedIds.Count == 0).
        var service = await unitOfWork.Services.GetByIdAsync(enquiry.ServiceId);

        var requestedIds = request.AgentIds.Distinct().ToList();
        var requestedAgents = new List<Agent>();
        if (requestedIds.Count > 0)
        {
            // Server-side service-scoping enforcement — the admin picker UI already filters to
            // service-mapped agents, but the API itself must not trust that: without this check an
            // admin could assign a completely unrelated agent by calling the endpoint directly.
            requestedAgents = await db.Agents
                .Include(a => a.AgentServices)
                .Where(a => requestedIds.Contains(a.Id) && a.IsActive)
                .ToListAsync();
            if (requestedAgents.Count != requestedIds.Count)
                return BadRequestResponse("One or more agents not found or inactive");

            if (requestedAgents.Any(a => !a.AgentServices.Any(x => x.ServiceId == enquiry.ServiceId)))
                return BadRequestResponse("One or more agents are not mapped to this enquiry's service");

            // An Agent is just a role on an existing consumer User account, not a separate identity
            // — mirrors the same guard in CreateEnquiry's auto-assign path. Rejected outright (not
            // silently dropped) since this is an explicit admin pick, not an automatic assignment.
            if (requestedAgents.Any(a => a.UserId == enquiry.UserId))
                return BadRequestResponse("An agent cannot be assigned to their own submitted enquiry");
        }

        var currentAssignments = await db.EnquiryAgents.Where(ia => ia.EnquiryId == id).ToListAsync();
        var currentIds = currentAssignments.Select(ia => ia.AgentId).ToHashSet();
        var newlyAddedIds = requestedIds.Where(agentId => !currentIds.Contains(agentId)).ToHashSet();
        var removedIds = currentIds.Except(requestedIds).ToHashSet();

        db.EnquiryAgents.RemoveRange(currentAssignments);
        var now = DateTime.UtcNow;
        db.EnquiryAgents.AddRange(requestedIds.Select(agentId => new EnquiryAgent
        {
            EnquiryId = id,
            AgentId = agentId,
            AssignedAt = now,
        }));

        // Single-combined-write auto-transition: the set becoming non-empty while the Enquiry is
        // Submitted flips it to Contacted as ONE EnquiryStatusHistory row in the SAME
        // SaveChangesAsync call as the assignment — never two independent admin actions/writes.
        // Clearing the set, or replacing an already-non-empty set, writes no history row.
        // Bypasses EnquiryStatusTransitions.IsAllowed — safe only because this only ever fires from
        // Submitted (checked above) and Submitted -> Contacted is unconditionally legal in the table.
        var autoTransitioned = requestedIds.Count > 0 && enquiry.Status == EnquiryStatuses.Submitted;
        if (autoTransitioned)
        {
            enquiry.Status = EnquiryStatuses.Contacted;
            db.EnquiryStatusHistories.Add(new EnquiryStatusHistory
            {
                Id = Guid.NewGuid(),
                EnquiryId = enquiry.Id,
                Status = EnquiryStatuses.Contacted,
                ChangedByAdminId = adminId,
                Note = "Auto-moved to Contacted on agent assignment",
                CreatedAt = now,
            });
        }
        enquiry.UpdatedAt = now;

        // Only the diffed-in agents get notified — an admin re-saving an already-assigned set (or
        // adding one new agent to an existing group) must never re-notify agents who were already
        // there. Written in the SAME SaveChangesAsync as the assignment so the inbox record can
        // never be lost even if RabbitMQ/FCM is down afterward.
        var notificationsToSend = new List<NotificationEvent>();
        foreach (var agent in requestedAgents.Where(a => newlyAddedIds.Contains(a.Id) && a.UserId != null))
        {
            var notification = BuildLeadAssignedNotification(agent, enquiry);
            db.NotificationEvents.Add(notification);
            notificationsToSend.Add(notification);
        }

        // A removed agent is told they lost the lead too — otherwise it just silently vanishes from
        // their My Leads next refresh with no explanation. Routes to the leads list, not lead detail
        // (GetMyLeadDetail would now 403 them).
        var removedAgents = removedIds.Count > 0
            ? await db.Agents.Where(a => removedIds.Contains(a.Id)).ToListAsync()
            : [];
        foreach (var removedAgent in removedAgents.Where(a => a.UserId != null))
        {
            var notification = new NotificationEvent
            {
                Id = Guid.NewGuid(),
                TargetUserId = removedAgent.UserId!.Value,
                Type = NotificationTypes.LeadUnassigned,
                Title = "Removed from lead",
                Body = $"You are no longer assigned to a lead for '{service!.Name}'.",
                ActionRoute = "/my-leads",
                CreatedAt = DateTime.UtcNow,
            };
            db.NotificationEvents.Add(notification);
            notificationsToSend.Add(notification);
        }

        // The consumer is told their enquiry's agent set changed even when it doesn't also trigger
        // the Submitted->Contacted auto-transition below (which already carries its own consumer
        // push) — otherwise a change to an already-Contacted+ enquiry's agents is invisible to them.
        // Fires even when clearing to zero: the lead is now exactly as unattended as CreateEnquiry's
        // zero-mapped-agent case, and silence would leave the consumer thinking someone still has it.
        var netChanged = newlyAddedIds.Count > 0 || removedIds.Count > 0;
        if (!autoTransitioned && netChanged)
        {
            var body = requestedIds.Count > 0
                ? $"The agent assigned to your enquiry for '{service!.Name}' has changed."
                : $"Your enquiry for '{service!.Name}' currently has no agent assigned — our team will follow up shortly.";
            var notification = new NotificationEvent
            {
                Id = Guid.NewGuid(),
                TargetUserId = enquiry.UserId,
                Type = NotificationTypes.EnquiryAgentChanged,
                Title = "Update on your enquiry",
                Body = body,
                ActionRoute = "/enquiry-detail",
                ActionArgumentsJson = JsonSerializer.Serialize(new { id = id.ToString() }),
                CreatedAt = DateTime.UtcNow,
            };
            db.NotificationEvents.Add(notification);
            notificationsToSend.Add(notification);
        }

        try
        {
            await unitOfWork.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            // Two distinct races land here: an agent updating this lead's status concurrently
            // (caught via Enquiry's xmin token), or a second concurrent AdminSetEnquiryAgents call
            // on the same enquiry (caught via the RemoveRange below hitting 0 rows the second time,
            // since EnquiryAgent itself carries no concurrency token — its composite key is the
            // signal). Either way: never silently clobber the other request's change.
            return ConflictResponse("This enquiry was just updated by another request. Please retry.", "CONCURRENT_UPDATE");
        }

        var updated = await unitOfWork.Enquiries.GetByIdWithDetailsAsync(id);

        // Fires only when autoTransitioned, mirroring AdminUpdateEnquiryStatus's push — replacing an
        // already-non-empty set (or clearing it) pushes nothing here.
        if (autoTransitioned)
        {
            await PushEnquiryStatusChangedAsync(hubContext, enquiry.UserId, id, enquiry.Status, agentAssigned: true);
            await PublishEnquiryStatusPushAsync(publisher, enquiry.UserId, id, updated!.Service.Name, enquiry.Status);
        }

        // Entirely independent of, and additional to, the block above — a lead-assignment,
        // lead-removal, or agent-changed notification fires per affected recipient, regardless of
        // whether the set-change happened to also auto-transition the status.
        await SendNotificationEventsAsync(hubContext, publisher, notificationsToSend);

        return OkResponse(updated!.Adapt<EnquiryDetailDto>());
    }

    // Admin's side of the escalation flow — no request body, resolves whichever escalation is
    // currently Pending for this enquiry (the partial unique index guarantees there's at most one).
    public static async Task<IResult> AdminResolveEscalation(
        Guid id, ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db,
        IHubContext<EnquiryHub> hubContext, IRabbitMqPublisher publisher)
    {
        if (!AdminAuthHandlers.TryGetAdminId(principal, out var adminId))
            return UnauthorizedResponse();

        var escalation = await db.EnquiryEscalations
            .FirstOrDefaultAsync(esc => esc.EnquiryId == id && esc.Status == "Pending");
        if (escalation == null) return NotFoundResponse("No pending escalation for this enquiry");

        // Needed for the notification body below — fetched before the save (same transaction),
        // not after, since the escalation mutation below doesn't touch the Enquiry row itself.
        var enquiryForNotification = await unitOfWork.Enquiries.GetByIdWithDetailsAsync(id);

        escalation.Status = "Resolved";
        escalation.ResolvedAt = DateTime.UtcNow;
        escalation.ResolvedByAdminId = adminId;

        var notification = new NotificationEvent
        {
            Id = Guid.NewGuid(),
            TargetUserId = enquiryForNotification!.UserId,
            Type = NotificationTypes.EscalationResolved,
            Title = "Your report was resolved",
            Body = $"Admin has resolved your report about the agent for '{enquiryForNotification.Service.Name}'.",
            ActionRoute = "/enquiry-detail",
            ActionArgumentsJson = JsonSerializer.Serialize(new { id = id.ToString() }),
            CreatedAt = DateTime.UtcNow,
        };
        db.NotificationEvents.Add(notification);

        await unitOfWork.SaveChangesAsync();

        var updated = await unitOfWork.Enquiries.GetByIdWithDetailsAsync(id);
        await SendNotificationEventsAsync(hubContext, publisher, [notification]);

        return OkResponse(updated!.Adapt<EnquiryDetailDto>());
    }

    // ── Push helpers ─────────────────────────────────────────────────────────

    // Best-effort — a SignalR push failure must never turn an already-committed status update into
    // an error response. Only called from a point where the caller's own commit is already final.
    // Clone of GoLiveHandlers.PushWalletBalanceChangedAsync's shape.
    private static async Task PushEnquiryStatusChangedAsync(
        IHubContext<EnquiryHub> hubContext, Guid userId, Guid enquiryId, string status, bool agentAssigned)
    {
        try
        {
            await hubContext.Clients.Group($"user_{userId}").SendAsync("EnquiryStatusChanged", new
            {
                enquiryId,
                status,
                agentAssigned,
            });
        }
        catch { }
    }

    // Fire-and-forget by design: a slow/failed publish here must never block the admin's response.
    // The consumer worker resolves device tokens and sends the actual push independently (see
    // EnquiryStatusPushWorkerService). Clone of ChatHandlers.PublishPushNotificationAsync's shape.
    private static async Task PublishEnquiryStatusPushAsync(
        IRabbitMqPublisher publisher, Guid userId, Guid enquiryId, string serviceName, string status, bool forAgent = false)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new EnquiryStatusPushPayload(userId, enquiryId, serviceName, status, forAgent));
            await publisher.PublishAsync("enquiry.status.push", payload);
        }
        catch
        {
            // Never let a push-notification publish failure fail the status update itself.
        }
    }

    // Reuses PushEnquiryStatusChangedAsync/PublishEnquiryStatusPushAsync verbatim, once per agent's
    // UserId, instead of a separate pipeline — a co-assigned agent's live status-change signal is
    // identical in shape to the consumer's, just addressed to a different recipient. excludeAgentId
    // skips whichever agent (if any) just made this exact change themselves — they already know.
    private static async Task NotifyCoAssignedAgentsOfStatusChangeAsync(
        IHubContext<EnquiryHub> hubContext, IRabbitMqPublisher publisher, Enquiry updated, Guid? excludeAgentId = null)
    {
        foreach (var ia in updated.EnquiryAgents)
        {
            if (excludeAgentId.HasValue && ia.AgentId == excludeAgentId.Value) continue;
            if (ia.Agent?.UserId is not { } agentUserId) continue;
            await PushEnquiryStatusChangedAsync(hubContext, agentUserId, updated.Id, updated.Status, agentAssigned: true);
            await PublishEnquiryStatusPushAsync(publisher, agentUserId, updated.Id, updated.Service.Name, updated.Status, forAgent: true);
        }
    }

    // Shared by both CreateEnquiry's auto-assign path and AdminSetEnquiryAgents's manual path — the
    // notification-construction logic lives in exactly one place.
    private static NotificationEvent BuildLeadAssignedNotification(Agent agent, Enquiry enquiry) => new()
    {
        Id = Guid.NewGuid(),
        TargetUserId = agent.UserId!.Value,
        Type = NotificationTypes.LeadAssigned,
        Title = "New lead assigned",
        Body = $"You have a new lead from {enquiry.FullName}.",
        ActionRoute = "/lead-detail",
        ActionArgumentsJson = JsonSerializer.Serialize(new { id = enquiry.Id.ToString() }),
        CreatedAt = DateTime.UtcNow,
    };

    // Generic dispatch for any batch of already-persisted NotificationEvent rows — despite the
    // original LeadAssigned-only name this replaced, the body never was type-specific; now also used
    // for EscalationResolved/LeadUnassigned/EnquiryAgentChanged.
    private static async Task SendNotificationEventsAsync(
        IHubContext<EnquiryHub> hubContext, IRabbitMqPublisher publisher, IEnumerable<NotificationEvent> notifications)
    {
        foreach (var notification in notifications)
        {
            await PushNotificationReceivedAsync(hubContext, notification.TargetUserId!.Value, notification);
            await PublishNotificationPushAsync(publisher, notification.Id, notification.TargetUserId!.Value);
        }
    }

    // Best-effort — mirrors PushEnquiryStatusChangedAsync's shape exactly, but deliberately generic:
    // this event name and payload shape are meant to be reused by every future NotificationEvent
    // producer, not just Agent lead-assignment.
    private static async Task PushNotificationReceivedAsync(IHubContext<EnquiryHub> hubContext, Guid userId, NotificationEvent notification)
    {
        try
        {
            await hubContext.Clients.Group($"user_{userId}").SendAsync("NotificationReceived", new
            {
                id = notification.Id,
                type = notification.Type,
                title = notification.Title,
                body = notification.Body,
                actionRoute = notification.ActionRoute,
            });
        }
        catch { }
    }

    // Fire-and-forget — NotificationPushWorkerService resolves device tokens and sends the actual
    // FCM push independently, loading Title/Body/ActionRoute/ActionArgumentsJson from the
    // already-persisted NotificationEvent row. Fully separate queue/pipeline from
    // PublishEnquiryStatusPushAsync — no shared code, no shared queue.
    private static async Task PublishNotificationPushAsync(IRabbitMqPublisher publisher, Guid notificationId, Guid userId)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new NotificationPushPayload(notificationId, userId));
            await publisher.PublishAsync("notification.push", payload);
        }
        catch
        {
            // Never let a push-notification publish failure fail the assignment itself.
        }
    }
}
