using System.Security.Claims;
using System.Text.Json;
using FluentValidation;
using Mapster;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using RentNearBy.Api.Hubs;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

public static class ChatHandlers
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    private const int NewConversationMaxPerDay = 10;
    private static readonly TimeSpan NewConversationWindow = TimeSpan.FromHours(24);
    private const int SendMessageMaxPerMinute = 20;
    private static readonly TimeSpan SendMessageWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan NewAccountCooldown = TimeSpan.FromHours(24);

    private const int DefaultPageSize = 30;

    // ── Conversations ────────────────────────────────────────────────────────

    public static async Task<IResult> GetConversations(
        ClaimsPrincipal principal, int? offset, int? limit,
        IUnitOfWork unitOfWork, ApplicationDbContext db)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var callerId)) return UnauthorizedResponse();

        var conversations = await unitOfWork.Conversations.GetForUserPagedAsync(
            callerId, offset ?? 0, Math.Clamp(limit ?? DefaultPageSize, 1, 100));

        // One batched lookup per listing type (2 queries total) covering both what
        // ReconcileLifecycleStatusBatchAsync needs (IsDeleted/IsActive) and what
        // BuildConversationDtoAsync needs (title/photo/subtype) — previously these were two
        // separate round-trips to the same RoomListings/PlotListings rows for the same ids.
        var listingInfo = await BuildListingLookupAsync(conversations, db);

        // Batch-correct lifecycle status for the whole page (both the DB rows and the
        // in-memory objects below) instead of relying on the old lazy-on-SendMessage-only
        // reconciliation — see ReconcileLifecycleStatusBatchAsync.
        await ReconcileLifecycleStatusBatchAsync(conversations, listingInfo, db);

        // Batch the other-party-name lookup that BuildConversationDtoAsync otherwise does per
        // row — turns an up-to-~61-query page load into ~4.
        var otherPartyIds = conversations.Select(c => c.OwnerId == callerId ? c.RenterId : c.OwnerId).Distinct().ToList();
        var otherPartyNames = await db.Users.Where(u => otherPartyIds.Contains(u.Id))
            .Select(u => new { u.Id, u.Name }).ToDictionaryAsync(u => u.Id, u => u.Name ?? "User");

        var dtos = new List<ConversationDto>(conversations.Count);
        foreach (var c in conversations)
            dtos.Add(await BuildConversationDtoAsync(c, callerId, db, otherPartyNames, listingInfo));

        return OkResponse(new { items = dtos });
    }

    public static async Task<IResult> CreateConversation(
        CreateConversationRequest request, IValidator<CreateConversationRequest> validator,
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, IRateLimitService rateLimiter,
        ApplicationDbContext db, HttpContext httpContext)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        if (!UsersHandlers.TryGetUserId(principal, out var callerId)) return UnauthorizedResponse();

        var caller = await unitOfWork.Users.GetByIdAsync(callerId);
        if (caller == null) return NotFoundResponse("User not found");

        if (DateTime.UtcNow - caller.CreatedAt < NewAccountCooldown)
            return ForbiddenResponse("New accounts can't start a chat yet — please try again in a bit.");

        var ownerId = request.ListingType == "Room"
            ? await db.RoomListings.Where(l => l.Id == request.ListingId && !l.IsDeleted).Select(l => (Guid?)l.UserId).FirstOrDefaultAsync()
            : await db.PlotListings.Where(l => l.Id == request.ListingId && !l.IsDeleted).Select(l => (Guid?)l.UserId).FirstOrDefaultAsync();

        if (ownerId == null) return NotFoundResponse("Listing not found");
        if (ownerId == callerId) return BadRequestResponse("You can't start a chat about your own listing");

        if (await unitOfWork.UserBlocks.ExistsAsync(ownerId.Value, callerId) ||
            await unitOfWork.UserBlocks.ExistsAsync(callerId, ownerId.Value))
            return ForbiddenResponse("You can't message this user");

        var existing = await unitOfWork.Conversations.FindExistingAsync(callerId, ownerId.Value, request.ListingType, request.ListingId);
        if (existing != null)
        {
            // Same staleness bug GetConversations had — reopening an existing conversation
            // whose listing has since been removed/deactivated should reflect that immediately,
            // not wait for a failed send. Single tracked row, cheap.
            await RefreshListingLifecycleStatusAsync(existing, db, unitOfWork);
            await unitOfWork.SaveChangesAsync();
            return OkResponse(await BuildConversationDtoAsync(existing, callerId, db));
        }

        var rl = await rateLimiter.CheckAsync($"chat:newconv:{callerId}", NewConversationMaxPerDay, NewConversationWindow);
        if (!rl.IsAllowed)
        {
            httpContext.Response.Headers["Retry-After"] = ((int)rl.RetryAfter!.Value.TotalSeconds).ToString();
            return TooManyRequestsResponse();
        }

        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            RenterId = callerId,
            OwnerId = ownerId.Value,
            ListingType = request.ListingType,
            ListingId = request.ListingId,
            Status = "Active",
            CreatedAt = DateTime.UtcNow,
            LastMessageAt = DateTime.UtcNow,
        };

        await unitOfWork.Conversations.AddAsync(conversation);
        try
        {
            await unitOfWork.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Lost a race against a concurrent create for the same renter+owner+listing
            // (double-tap, retry-on-timeout) — the unique index caught what the
            // check-then-act FindExistingAsync above couldn't. Same catch-and-retry
            // shape as ListingsHandlers' duplicate-report guard. Return the winning row.
            //
            // Deliberately NOT calling RefreshListingLifecycleStatusAsync + SaveChangesAsync
            // here (unlike the `existing != null` early-return above): the failed `conversation`
            // insert is still tracked as Added on this same DbContext after the DbUpdateException
            // above (EF Core doesn't roll back the change tracker on a failed save) — a second
            // SaveChangesAsync here would re-flush that same failed insert alongside winner's
            // update and hit the identical unique-index violation again, uncaught this time.
            // winner.Status is read verbatim (slightly stale in this rare race window) rather
            // than risk that.
            var winner = await unitOfWork.Conversations.FindExistingAsync(callerId, ownerId.Value, request.ListingType, request.ListingId);
            if (winner != null) return OkResponse(await BuildConversationDtoAsync(winner, callerId, db));
            throw;
        }

        return CreatedResponse(await BuildConversationDtoAsync(conversation, callerId, db), $"/api/v1/chat/conversations/{conversation.Id}");
    }

    // ── Messages ─────────────────────────────────────────────────────────────

    public static async Task<IResult> GetMessages(
        Guid conversationId, DateTime? before, int? limit,
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var callerId)) return UnauthorizedResponse();

        var conversation = await unitOfWork.Conversations.GetByIdAsync(conversationId);
        if (conversation == null) return NotFoundResponse("Conversation not found");
        if (conversation.RenterId != callerId && conversation.OwnerId != callerId) return ForbiddenResponse();

        // Same reconciliation SendMessage already does, applied here too so opening a thread
        // directly (not via a fresh GetConversations page) also shows the correct lifecycle
        // status immediately rather than waiting for a failed send attempt.
        await RefreshListingLifecycleStatusAsync(conversation, db, unitOfWork);
        await unitOfWork.SaveChangesAsync();

        var messages = await unitOfWork.Messages.GetPagedForConversationAsync(
            conversationId, before, Math.Clamp(limit ?? DefaultPageSize, 1, 100));

        var dtos = messages.Select(m => new MessageDto
        {
            Id = m.Id,
            ConversationId = m.ConversationId,
            SenderId = m.SenderId,
            IsMine = m.SenderId == callerId,
            Type = m.Type,
            PayloadJson = m.PayloadJson,
            RespondsToMessageId = m.RespondsToMessageId,
            ReadAt = m.ReadAt,
            CreatedAt = m.CreatedAt,
        }).ToList();

        return OkResponse(new { items = dtos, conversationStatus = conversation.Status });
    }

    public static async Task<IResult> SendMessage(
        Guid conversationId, SendMessageRequest request, IValidator<SendMessageRequest> validator,
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, IRateLimitService rateLimiter,
        IHubContext<ChatHub> hubContext, IRabbitMqPublisher publisher,
        ApplicationDbContext db, HttpContext httpContext, IMemoryCache cache)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        if (!UsersHandlers.TryGetUserId(principal, out var callerId)) return UnauthorizedResponse();

        var conversation = await unitOfWork.Conversations.GetByIdAsync(conversationId);
        if (conversation == null) return NotFoundResponse("Conversation not found");
        if (conversation.RenterId != callerId && conversation.OwnerId != callerId) return ForbiddenResponse();

        await RefreshListingLifecycleStatusAsync(conversation, db, unitOfWork);
        if (conversation.Status != "Active")
        {
            // RefreshListingLifecycleStatusAsync only marks the entity Modified in the
            // tracking context — without an explicit save here, the computed
            // ListingRemoved/ListingInactive flip never reaches the database, so the
            // Chats list and this conversation's header would keep showing "Active"
            // forever even though sends are (correctly) already being rejected below.
            await unitOfWork.SaveChangesAsync();
            return BadRequestResponse($"This conversation is no longer active ({conversation.Status})");
        }

        var otherPartyId = conversation.RenterId == callerId ? conversation.OwnerId : conversation.RenterId;

        if (await unitOfWork.UserBlocks.ExistsAsync(otherPartyId, callerId) ||
            await unitOfWork.UserBlocks.ExistsAsync(callerId, otherPartyId))
            return ForbiddenResponse("You can't message this user");

        var rl = await rateLimiter.CheckAsync($"chat:send:{callerId}", SendMessageMaxPerMinute, SendMessageWindow);
        if (!rl.IsAllowed)
        {
            httpContext.Response.Headers["Retry-After"] = ((int)rl.RetryAfter!.Value.TotalSeconds).ToString();
            return TooManyRequestsResponse();
        }

        if (request.RespondsToMessageId.HasValue)
        {
            var question = await unitOfWork.Messages.GetByIdAsync(request.RespondsToMessageId.Value);
            if (question == null || question.ConversationId != conversationId)
                return BadRequestResponse("That question no longer exists in this conversation");
            if (question.SenderId == callerId)
                return BadRequestResponse("You can't answer your own question");
        }

        var message = new Message
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            SenderId = callerId,
            Type = request.Type,
            PayloadJson = request.PayloadJson,
            RespondsToMessageId = request.RespondsToMessageId,
            CreatedAt = DateTime.UtcNow,
        };

        await unitOfWork.Messages.AddAsync(message);
        await ApplyToConversationAsync(conversation, callerId, otherPartyId, message, unitOfWork, cache);

        try
        {
            await unitOfWork.SaveChangesAsync();
        }
        catch (DbUpdateException) when (request.RespondsToMessageId.HasValue)
        {
            // Lost a race against a concurrent answer to the same question (double-tap on
            // a reply option) — the partial unique index on RespondsToMessageId caught it.
            // Same catch-and-return-the-winner shape as the Conversations unique-index guard.
            var existingAnswer = await db.Messages.FirstOrDefaultAsync(m => m.RespondsToMessageId == request.RespondsToMessageId.Value);
            if (existingAnswer != null)
            {
                return OkResponse(new MessageDto
                {
                    Id = existingAnswer.Id, ConversationId = existingAnswer.ConversationId, SenderId = existingAnswer.SenderId,
                    IsMine = existingAnswer.SenderId == callerId, Type = existingAnswer.Type, PayloadJson = existingAnswer.PayloadJson,
                    RespondsToMessageId = existingAnswer.RespondsToMessageId, CreatedAt = existingAnswer.CreatedAt,
                });
            }
            throw;
        }

        await PushMessageAsync(hubContext, conversation, message);
        await PublishPushNotificationAsync(publisher, unitOfWork, conversation, callerId, otherPartyId, message, cache);

        return CreatedResponse(new MessageDto
        {
            Id = message.Id, ConversationId = conversationId, SenderId = callerId, IsMine = true,
            Type = message.Type, PayloadJson = message.PayloadJson, RespondsToMessageId = message.RespondsToMessageId,
            CreatedAt = message.CreatedAt,
        }, $"/api/v1/chat/conversations/{conversationId}/messages/{message.Id}");
    }

    public static async Task<IResult> MarkRead(
        Guid conversationId, ClaimsPrincipal principal, IUnitOfWork unitOfWork, IHubContext<ChatHub> hubContext)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var callerId)) return UnauthorizedResponse();

        var conversation = await unitOfWork.Conversations.GetByIdAsync(conversationId);
        if (conversation == null) return NotFoundResponse("Conversation not found");
        if (conversation.RenterId != callerId && conversation.OwnerId != callerId) return ForbiddenResponse();

        await unitOfWork.Messages.MarkReadBulkAsync(conversationId, callerId);

        if (conversation.RenterId == callerId) conversation.UnreadCountForRenter = 0;
        else conversation.UnreadCountForOwner = 0;
        await unitOfWork.Conversations.UpdateAsync(conversation);
        await unitOfWork.SaveChangesAsync();

        await hubContext.Clients.Group($"conversation_{conversationId}")
            .SendAsync("MessagesRead", new { conversationId, readByUserId = callerId });

        return OkResponse(new { success = true });
    }

    // ── Contact request/response ────────────────────────────────────────────

    public static async Task<IResult> RespondContact(
        Guid messageId, RespondContactRequest request,
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, IHubContext<ChatHub> hubContext, IMemoryCache cache)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var callerId)) return UnauthorizedResponse();

        var original = await unitOfWork.Messages.GetByIdAsync(messageId);
        if (original == null || original.Type != "contact_request") return NotFoundResponse("Contact request not found");

        var conversation = await unitOfWork.Conversations.GetByIdAsync(original.ConversationId);
        if (conversation == null) return NotFoundResponse("Conversation not found");
        // Only the owner side can approve/decline a request for their own contact details.
        if (conversation.OwnerId != callerId) return ForbiddenResponse();

        var owner = await unitOfWork.Users.GetByIdAsync(callerId);
        if (owner == null) return NotFoundResponse("User not found");

        var payload = request.Approve
            ? JsonSerializer.Serialize(new { approved = true, phone = owner.PhoneNumber })
            : JsonSerializer.Serialize(new { approved = false });

        var response = new Message
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            SenderId = callerId,
            Type = "contact_response",
            PayloadJson = payload,
            CreatedAt = DateTime.UtcNow,
        };

        await unitOfWork.Messages.AddAsync(response);
        await ApplyToConversationAsync(conversation, callerId, conversation.RenterId, response, unitOfWork, cache);
        await unitOfWork.SaveChangesAsync();

        await PushMessageAsync(hubContext, conversation, response);

        return OkResponse(new MessageDto
        {
            Id = response.Id, ConversationId = conversation.Id, SenderId = callerId, IsMine = true,
            Type = response.Type, PayloadJson = response.PayloadJson, CreatedAt = response.CreatedAt,
        });
    }

    // ── Schedule propose/respond ────────────────────────────────────────────

    public static async Task<IResult> RespondSchedule(
        Guid messageId, RespondScheduleRequest request, IValidator<RespondScheduleRequest> validator,
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, IHubContext<ChatHub> hubContext, IMemoryCache cache)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        if (!UsersHandlers.TryGetUserId(principal, out var callerId)) return UnauthorizedResponse();

        var original = await unitOfWork.Messages.GetByIdAsync(messageId);
        if (original == null || original.Type != "schedule_proposal") return NotFoundResponse("Visit proposal not found");
        if (original.SenderId == callerId) return BadRequestResponse("You can't respond to your own proposal");

        var conversation = await unitOfWork.Conversations.GetByIdAsync(original.ConversationId);
        if (conversation == null) return NotFoundResponse("Conversation not found");
        if (conversation.RenterId != callerId && conversation.OwnerId != callerId) return ForbiddenResponse();

        var otherPartyId = conversation.RenterId == callerId ? conversation.OwnerId : conversation.RenterId;

        if (request.Action == "counter")
        {
            // Mark the original proposal superseded — stays visible, faded, no longer actionable.
            // original.PayloadJson was written by whichever client sent the initial proposal;
            // SendMessageRequestValidator only checks NotEmpty/MaxLength, not that it's valid
            // JSON, so this must degrade gracefully rather than 500 on a malformed payload.
            try
            {
                using var doc = JsonDocument.Parse(original.PayloadJson);
                var dict = doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.ToString());
                dict["status"] = "superseded";
                original.PayloadJson = JsonSerializer.Serialize(dict);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                original.PayloadJson = JsonSerializer.Serialize(new { status = "superseded" });
            }
            await unitOfWork.Messages.UpdateAsync(original);

            var counter = new Message
            {
                Id = Guid.NewGuid(),
                ConversationId = conversation.Id,
                SenderId = callerId,
                Type = "schedule_proposal",
                PayloadJson = JsonSerializer.Serialize(new { proposedAts = request.ProposedAts, status = "pending", supersedes = messageId }),
                CreatedAt = DateTime.UtcNow,
            };
            await unitOfWork.Messages.AddAsync(counter);
            await ApplyToConversationAsync(conversation, callerId, otherPartyId, counter, unitOfWork, cache);
            await unitOfWork.SaveChangesAsync();

            await PushMessageAsync(hubContext, conversation, counter);
            return OkResponse(new MessageDto
            {
                Id = counter.Id, ConversationId = conversation.Id, SenderId = callerId, IsMine = true,
                Type = counter.Type, PayloadJson = counter.PayloadJson, CreatedAt = counter.CreatedAt,
            });
        }

        object responsePayload;
        if (request.Action == "accept")
        {
            // Confirm the accepted time was actually one of the originally offered
            // slots — defends against a client sending a time that was never offered.
            // Degrades gracefully (rejects rather than 500s) if the original proposal's
            // payload is malformed, same shape as the "counter" branch's parse above.
            var offeredTimes = new List<DateTime>();
            try
            {
                using var doc = JsonDocument.Parse(original.PayloadJson);
                if (doc.RootElement.TryGetProperty("proposedAts", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    foreach (var el in arr.EnumerateArray())
                        if (el.TryGetDateTime(out var dt)) offeredTimes.Add(dt);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }

            if (!offeredTimes.Any(t => t == request.AcceptedAt!.Value))
                return BadRequestResponse("That time wasn't one of the offered slots");

            responsePayload = new { status = "accepted", confirmedAt = request.AcceptedAt, respondsTo = messageId };
        }
        else
        {
            responsePayload = new { status = "declined", respondsTo = messageId };
        }

        var response = new Message
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            SenderId = callerId,
            Type = "schedule_response",
            PayloadJson = JsonSerializer.Serialize(responsePayload),
            CreatedAt = DateTime.UtcNow,
        };

        await unitOfWork.Messages.AddAsync(response);
        await ApplyToConversationAsync(conversation, callerId, otherPartyId, response, unitOfWork, cache);
        await unitOfWork.SaveChangesAsync();

        await PushMessageAsync(hubContext, conversation, response);
        return OkResponse(new MessageDto
        {
            Id = response.Id, ConversationId = conversation.Id, SenderId = callerId, IsMine = true,
            Type = response.Type, PayloadJson = response.PayloadJson, CreatedAt = response.CreatedAt,
        });
    }

    // ── Block ────────────────────────────────────────────────────────────────

    public static async Task<IResult> BlockUser(Guid userId, ClaimsPrincipal principal, IUnitOfWork unitOfWork)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var callerId)) return UnauthorizedResponse();
        if (callerId == userId) return BadRequestResponse("You can't block yourself");

        if (!await unitOfWork.UserBlocks.ExistsAsync(callerId, userId))
        {
            await unitOfWork.UserBlocks.AddAsync(new UserBlock
            {
                Id = Guid.NewGuid(), BlockerId = callerId, BlockedId = userId, CreatedAt = DateTime.UtcNow,
            });
        }

        // A block is between two people, not one listing thread — flip every
        // conversation between this pair so the chat header and the conversations
        // list both reflect it immediately, not just future SendMessage calls.
        var conversations = await unitOfWork.Conversations.GetAllBetweenUsersAsync(callerId, userId);
        foreach (var conversation in conversations)
        {
            if (conversation.Status == "Blocked") continue;
            conversation.Status = "Blocked";
            await unitOfWork.Conversations.UpdateAsync(conversation);
        }

        try
        {
            await unitOfWork.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Lost a race against a concurrent block attempt on the same pair (double-tap) —
            // the unique index on UserBlocks(BlockerId, BlockedId) caught it. The desired end
            // state (blocked) is already achieved by the other request, so this is a no-op
            // success, not an error — same idempotent-on-conflict shape used elsewhere in
            // this file (e.g. CreateConversation's unique-index race).
        }
        return NoContentResponse();
    }

    public static async Task<IResult> UnblockUser(Guid userId, ClaimsPrincipal principal, IUnitOfWork unitOfWork)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var callerId)) return UnauthorizedResponse();
        if (callerId == userId) return BadRequestResponse("You can't unblock yourself");

        var existing = await unitOfWork.UserBlocks.GetByBlockerAndBlockedAsync(callerId, userId);
        if (existing == null) return NoContentResponse(); // caller never blocked this user — nothing to undo

        await unitOfWork.UserBlocks.DeleteAsync(existing);

        // Deliberate user action — unlike the listing-lifecycle reconciler (which
        // intentionally never overrides a "Blocked" status automatically), an explicit
        // unblock is exactly the case that should restore it. If the listing has since
        // gone inactive/removed, the next SendMessage/GetConversations reconciliation
        // pass will correct it from here, same as it would for any other conversation.
        // Only reaches here when `existing` was real — a caller who never blocked this
        // user (e.g. the other party trying to clear a block they don't own) can't flip
        // Status back just by hitting this endpoint.
        var conversations = await unitOfWork.Conversations.GetAllBetweenUsersAsync(callerId, userId);
        foreach (var conversation in conversations)
        {
            if (conversation.Status != "Blocked") continue;
            conversation.Status = "Active";
            await unitOfWork.Conversations.UpdateAsync(conversation);
        }

        await unitOfWork.SaveChangesAsync();
        return NoContentResponse();
    }

    // ── Question templates (shared GET — also mapped under /admin) ─────────

    public static async Task<IResult> GetQuestionTemplates(IUnitOfWork unitOfWork, IMemoryCache cache)
        => OkResponse(await GetCachedQuestionTemplatesAsync(unitOfWork, cache));

    public static async Task<IResult> CreateQuestionTemplate(
        CreateQuestionTemplateRequest request, IValidator<CreateQuestionTemplateRequest> validator,
        IUnitOfWork unitOfWork, IMemoryCache cache, ApplicationDbContext db)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        if (request.RoomTypeId.HasValue && !await db.RoomTypes.AnyAsync(r => r.Id == request.RoomTypeId.Value))
            return BadRequestResponse("Room type not found");
        if (request.PlotTypeId.HasValue && !await db.PlotTypes.AnyAsync(p => p.Id == request.PlotTypeId.Value))
            return BadRequestResponse("Plot type not found");

        var template = new QuestionTemplate
        {
            Id = Guid.NewGuid(),
            Key = request.Key.Trim(),
            ListingType = request.ListingType,
            RoomTypeId = request.RoomTypeId,
            PlotTypeId = request.PlotTypeId,
            QuestionText = request.QuestionText.Trim(),
            AnswerOptionsJson = request.AnswerOptionsJson,
            SortOrder = request.SortOrder,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };

        await unitOfWork.QuestionTemplates.AddAsync(template);
        await unitOfWork.SaveChangesAsync();

        cache.Remove("question_templates");

        return CreatedResponse(template.Adapt<QuestionTemplateDto>(), $"/api/v1/admin/question-templates/{template.Id}");
    }

    public static async Task<IResult> UpdateQuestionTemplate(
        Guid id, UpdateQuestionTemplateRequest request, IValidator<UpdateQuestionTemplateRequest> validator,
        IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var template = await unitOfWork.QuestionTemplates.GetByIdAsync(id);
        if (template == null) return NotFoundResponse("Question template not found");

        if (request.QuestionText != null) template.QuestionText = request.QuestionText.Trim();
        if (request.AnswerOptionsJson != null) template.AnswerOptionsJson = request.AnswerOptionsJson;
        if (request.SortOrder.HasValue) template.SortOrder = request.SortOrder.Value;

        await unitOfWork.QuestionTemplates.UpdateAsync(template);
        await unitOfWork.SaveChangesAsync();

        cache.Remove("question_templates");

        return OkResponse(template.Adapt<QuestionTemplateDto>());
    }

    // Deactivate, not hard-delete — Message.PayloadJson references a template's Key from
    // historical messages, so removing a template outright would break rendering old
    // conversations. Mirrors the Districts isActive-toggle pattern, not ReportReasons'
    // hard-delete (that pattern doesn't fit here since nothing else references a reason
    // by a stable key the way chat messages do).
    public static async Task<IResult> ToggleQuestionTemplateActive(
        Guid id, ToggleQuestionTemplateActiveRequest request, IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        var template = await unitOfWork.QuestionTemplates.GetByIdAsync(id);
        if (template == null) return NotFoundResponse("Question template not found");

        template.IsActive = request.IsActive;
        await unitOfWork.QuestionTemplates.UpdateAsync(template);
        await unitOfWork.SaveChangesAsync();

        cache.Remove("question_templates");

        return OkResponse(new { success = true, isActive = template.IsActive });
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private readonly record struct ListingInfoLite(string Title, string? Photo, Guid? RoomTypeId, Guid? PlotTypeId, bool? IsDeleted, bool? IsActive);

    // otherPartyNames/listingInfo are optional page-level lookups built once by GetConversations
    // (see BuildListingInfoLookupAsync below) to avoid 2 queries per row on a paginated list.
    // When omitted (CreateConversation's single-conversation call sites), falls back to the
    // original per-row queries — those call sites already reconcile lifecycle status themselves
    // before calling this, so c.Status is read verbatim and is correct either way.
    private static async Task<ConversationDto> BuildConversationDtoAsync(
        Conversation c, Guid callerId, ApplicationDbContext db,
        IReadOnlyDictionary<Guid, string>? otherPartyNames = null,
        IReadOnlyDictionary<(string ListingType, Guid ListingId), ListingInfoLite>? listingInfo = null)
    {
        var isOwner = c.OwnerId == callerId;
        var otherPartyId = isOwner ? c.RenterId : c.OwnerId;
        var otherPartyName = otherPartyNames != null
            ? otherPartyNames.GetValueOrDefault(otherPartyId, "User")
            : await db.Users.Where(u => u.Id == otherPartyId).Select(u => u.Name).FirstOrDefaultAsync() ?? "User";

        string listingTitle;
        string? thumbnailUrl;
        Guid? roomTypeId = null;
        Guid? plotTypeId = null;

        if (listingInfo != null && listingInfo.TryGetValue((c.ListingType, c.ListingId), out var info))
        {
            listingTitle = info.Title;
            thumbnailUrl = info.Photo;
            roomTypeId = info.RoomTypeId;
            plotTypeId = info.PlotTypeId;
        }
        else if (c.ListingType == "Room")
        {
            var l = await db.RoomListings.Where(x => x.Id == c.ListingId)
                .Select(x => new { x.RoomType.Name, x.RoomTypeId, Photo = x.Photos.OrderBy(p => p.PhotoOrder).Select(p => p.PhotoUrl).FirstOrDefault() })
                .FirstOrDefaultAsync();
            listingTitle = l?.Name ?? "Room";
            thumbnailUrl = l?.Photo;
            roomTypeId = l?.RoomTypeId;
        }
        else
        {
            var l = await db.PlotListings.Where(x => x.Id == c.ListingId)
                .Select(x => new { x.PlotType.Name, x.PlotTypeId, Photo = x.Photos.OrderBy(p => p.PhotoOrder).Select(p => p.PhotoUrl).FirstOrDefault() })
                .FirstOrDefaultAsync();
            listingTitle = l?.Name ?? "Plot";
            thumbnailUrl = l?.Photo;
            plotTypeId = l?.PlotTypeId;
        }

        return new ConversationDto
        {
            Id = c.Id,
            ListingType = c.ListingType,
            ListingId = c.ListingId,
            RoomTypeId = roomTypeId,
            PlotTypeId = plotTypeId,
            ListingTitle = listingTitle,
            ListingThumbnailUrl = thumbnailUrl,
            OtherPartyId = otherPartyId,
            OtherPartyName = otherPartyName,
            IsOwner = isOwner,
            Status = c.Status,
            LastMessageAt = c.LastMessageAt,
            LastMessagePreview = c.LastMessagePreview,
            UnreadCount = isOwner ? c.UnreadCountForOwner : c.UnreadCountForRenter,
        };
    }

    // Batches BOTH what BuildConversationDtoAsync needs (title/photo/subtype) AND what
    // ReconcileLifecycleStatusBatchAsync needs (IsDeleted/IsActive) for a whole GetConversations
    // page into 2 queries total (one per listing type), instead of one per conversation and
    // instead of two separate round-trips to the same rows for two different purposes.
    private static async Task<Dictionary<(string, Guid), ListingInfoLite>> BuildListingLookupAsync(
        IReadOnlyList<Conversation> conversations, ApplicationDbContext db)
    {
        var result = new Dictionary<(string, Guid), ListingInfoLite>();

        var roomIds = conversations.Where(c => c.ListingType == "Room").Select(c => c.ListingId).Distinct().ToList();
        if (roomIds.Count > 0)
        {
            var rows = await db.RoomListings.Where(l => roomIds.Contains(l.Id))
                .Select(l => new { l.Id, l.RoomType.Name, l.RoomTypeId, l.IsDeleted, l.IsActive, Photo = l.Photos.OrderBy(p => p.PhotoOrder).Select(p => p.PhotoUrl).FirstOrDefault() })
                .ToListAsync();
            foreach (var r in rows)
                result[("Room", r.Id)] = new ListingInfoLite(r.Name ?? "Room", r.Photo, r.RoomTypeId, null, r.IsDeleted, r.IsActive);
        }

        var plotIds = conversations.Where(c => c.ListingType == "Plot").Select(c => c.ListingId).Distinct().ToList();
        if (plotIds.Count > 0)
        {
            var rows = await db.PlotListings.Where(l => plotIds.Contains(l.Id))
                .Select(l => new { l.Id, l.PlotType.Name, l.PlotTypeId, l.IsDeleted, l.IsActive, Photo = l.Photos.OrderBy(p => p.PhotoOrder).Select(p => p.PhotoUrl).FirstOrDefault() })
                .ToListAsync();
            foreach (var r in rows)
                result[("Plot", r.Id)] = new ListingInfoLite(r.Name ?? "Plot", r.Photo, null, r.PlotTypeId, r.IsDeleted, r.IsActive);
        }

        return result;
    }

    // Batch version of RefreshListingLifecycleStatusAsync for a whole GetConversations page,
    // consuming the lookup BuildListingLookupAsync already built (no extra queries here) — then
    // a handful of grouped ExecuteUpdateAsync bulk writes (one per distinct target status among
    // only the rows that actually changed). No entity tracking, safe against the page's
    // AsNoTracking() load, and both the DB and the in-memory objects end up correct in the same
    // request (not just the API response).
    private static async Task ReconcileLifecycleStatusBatchAsync(
        IReadOnlyList<Conversation> conversations, IReadOnlyDictionary<(string, Guid), ListingInfoLite> listingInfo, ApplicationDbContext db)
    {
        var byTargetStatus = new Dictionary<string, List<Guid>>();
        foreach (var c in conversations)
        {
            if (c.Status == "Blocked") continue; // block always wins, never auto-reconciled away
            listingInfo.TryGetValue((c.ListingType, c.ListingId), out var info);
            var desired = ComputeLifecycleStatus(info.IsDeleted, info.IsActive);
            if (c.Status == desired) continue;

            if (!byTargetStatus.TryGetValue(desired, out var ids))
                byTargetStatus[desired] = ids = new List<Guid>();
            ids.Add(c.Id);

            // In-memory correction only (this page was loaded AsNoTracking) — the
            // ExecuteUpdateAsync calls below are what actually persist it.
            c.Status = desired;
        }

        foreach (var (status, ids) in byTargetStatus)
        {
            await db.Conversations.Where(x => ids.Contains(x.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, status));
        }
    }

    // Denormalized fields updated in lockstep with every message — keeps the
    // Chats-list screen a single indexed query against Conversations, no join
    // against Messages needed for the common case.
    private static async Task ApplyToConversationAsync(Conversation conversation, Guid senderId, Guid recipientId, Message message, IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        conversation.LastMessageAt = message.CreatedAt;
        conversation.LastMessagePreview = await BuildPreviewAsync(message, unitOfWork, cache);

        if (recipientId == conversation.RenterId) conversation.UnreadCountForRenter++;
        else conversation.UnreadCountForOwner++;

        await unitOfWork.Conversations.UpdateAsync(conversation);
    }

    // Shares the same "question_templates" cache entry GetQuestionTemplates already
    // maintains (invalidated on create/update/toggle, see those handlers) — was previously an
    // uncached unitOfWork.QuestionTemplates.GetAllAsync() DB hit on every quick_reply preview
    // build (up to twice per SendMessage: here and in PublishPushNotificationAsync).
    private static async Task<List<QuestionTemplateDto>> GetCachedQuestionTemplatesAsync(IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        if (!cache.TryGetValue("question_templates", out List<QuestionTemplateDto>? cached) || cached == null)
        {
            var templates = await unitOfWork.QuestionTemplates.GetAllAsync();
            cached = templates.OrderBy(t => t.SortOrder).Select(t => t.Adapt<QuestionTemplateDto>()).ToList();
            cache.Set("question_templates", cached, CacheTtl);
        }
        return cached;
    }

    private static async Task<string> BuildPreviewAsync(Message message, IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        try
        {
            switch (message.Type)
            {
                case "quick_reply":
                    using (var doc = JsonDocument.Parse(message.PayloadJson))
                    {
                        if (doc.RootElement.TryGetProperty("key", out var keyEl))
                        {
                            var templates = await GetCachedQuestionTemplatesAsync(unitOfWork, cache);
                            var match = templates.FirstOrDefault(t => t.Key == keyEl.GetString());
                            if (match != null) return match.QuestionText;
                        }
                    }
                    return "New message";
                case "contact_request": return "Contact number requested";
                case "contact_response":
                    using (var doc = JsonDocument.Parse(message.PayloadJson))
                        return doc.RootElement.TryGetProperty("approved", out var a) && a.GetBoolean()
                            ? "Contact shared" : "Contact request declined";
                case "schedule_proposal": return "Visit proposed";
                case "schedule_response":
                    using (var doc = JsonDocument.Parse(message.PayloadJson))
                        return doc.RootElement.TryGetProperty("status", out var s) && s.GetString() == "accepted"
                            ? "Visit confirmed" : "Visit declined";
                default: return "New message";
            }
        }
        catch (JsonException)
        {
            return "New message";
        }
    }

    private static async Task PushMessageAsync(IHubContext<ChatHub> hubContext, Conversation conversation, Message message)
    {
        var dto = new MessageDto
        {
            Id = message.Id, ConversationId = message.ConversationId, SenderId = message.SenderId,
            IsMine = false, // recipient's client compares SenderId against its own userId itself
            Type = message.Type, PayloadJson = message.PayloadJson, CreatedAt = message.CreatedAt,
        };
        await hubContext.Clients.Group($"conversation_{conversation.Id}").SendAsync("MessageReceived", dto);

        var recipientId = message.SenderId == conversation.RenterId ? conversation.OwnerId : conversation.RenterId;
        var unread = recipientId == conversation.RenterId ? conversation.UnreadCountForRenter : conversation.UnreadCountForOwner;
        await hubContext.Clients.Group($"user_{recipientId}")
            .SendAsync("UnreadCountChanged", new { conversationId = conversation.Id, unreadCount = unread });
    }

    // Fire-and-forget by design: a slow/failed publish here must never block the
    // sender's response. The consumer worker resolves device tokens and sends the
    // actual push independently (see ChatMessageNotificationWorkerService).
    private static async Task PublishPushNotificationAsync(
        IRabbitMqPublisher publisher, IUnitOfWork unitOfWork, Conversation conversation,
        Guid senderId, Guid recipientId, Message message, IMemoryCache cache)
    {
        try
        {
            var sender = await unitOfWork.Users.GetByIdAsync(senderId);
            var preview = await BuildPreviewAsync(message, unitOfWork, cache);
            var payload = JsonSerializer.Serialize(new
            {
                recipientUserId = recipientId,
                conversationId = conversation.Id,
                senderName = sender?.Name ?? "Someone",
                preview,
            });
            await publisher.PublishAsync("chat.message.push", payload);
        }
        catch
        {
            // Never let a push-notification publish failure fail the message send itself.
        }
    }

    // Lazily reconciles a conversation's Status against its underlying listing's
    // current lifecycle state — cheaper than hooking every existing delete/deactivate
    // handler in RoomListingsHandlers/PlotHandlers, and keeps this feature's file
    // footprint isolated from those unrelated handlers.
    private static async Task RefreshListingLifecycleStatusAsync(Conversation conversation, ApplicationDbContext db, IUnitOfWork unitOfWork)
    {
        if (conversation.Status is "Blocked") return; // block always wins, never auto-reconciled away

        bool? isDeleted;
        bool? isActive;
        if (conversation.ListingType == "Room")
        {
            var l = await db.RoomListings.Where(x => x.Id == conversation.ListingId)
                .Select(x => new { x.IsDeleted, x.IsActive }).FirstOrDefaultAsync();
            isDeleted = l?.IsDeleted; isActive = l?.IsActive;
        }
        else
        {
            var l = await db.PlotListings.Where(x => x.Id == conversation.ListingId)
                .Select(x => new { x.IsDeleted, x.IsActive }).FirstOrDefaultAsync();
            isDeleted = l?.IsDeleted; isActive = l?.IsActive;
        }

        var desired = ComputeLifecycleStatus(isDeleted, isActive);
        if (conversation.Status != desired)
        {
            conversation.Status = desired;
            await unitOfWork.Conversations.UpdateAsync(conversation);
        }
    }

    // Shared by RefreshListingLifecycleStatusAsync (single-row, persisting) and
    // ReconcileLifecycleStatusBatchAsync (page-batch, persisting via bulk update) — pure rule,
    // no I/O. Caller is responsible for excluding "Blocked" conversations before calling this
    // (block always wins, never auto-reconciled away), so it isn't a parameter here.
    private static string ComputeLifecycleStatus(bool? isDeleted, bool? isActive) =>
        isDeleted is null or true ? "ListingRemoved" : isActive == false ? "ListingInactive" : "Active";
}
