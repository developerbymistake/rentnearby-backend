using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentValidation;
using Mapster;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using RentNearBy.Api.Extensions;
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
    // Separate, more generous bucket than SendMessage's — MarkRead is legitimately called on
    // every conversation-open and every incoming message while a thread is open, so sharing
    // SendMessage's tighter budget would risk throttling normal use in a busy conversation.
    private const int MarkReadMaxPerMinute = 60;
    // TEMPORARY for local dev/testing — revert to TimeSpan.FromHours(1) before shipping.
    private static readonly TimeSpan NewAccountCooldown = TimeSpan.FromMinutes(5);

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

        var accountAge = DateTime.UtcNow - caller.CreatedAt;
        if (accountAge < NewAccountCooldown)
        {
            var remainingMinutes = Math.Max(1, (int)Math.Ceiling((NewAccountCooldown - accountAge).TotalMinutes));
            return ForbiddenResponse($"You have a new account, so you can't chat yet. Try again in {remainingMinutes} min.");
        }

        // IsActive fetched alongside the owner lookup — a fresh conversation about a
        // deactivated (but not deleted) listing must be created with its TRUE initial status,
        // not a hardcoded "Active" that only gets corrected on the first SendMessage attempt.
        Guid? ownerId;
        bool? listingIsActive;
        if (request.ListingType == "Room")
        {
            var l = await db.RoomListings.Where(x => x.Id == request.ListingId && !x.IsDeleted)
                .Select(x => new { x.UserId, x.IsActive }).FirstOrDefaultAsync();
            ownerId = l?.UserId; listingIsActive = l?.IsActive;
        }
        else
        {
            var l = await db.PlotListings.Where(x => x.Id == request.ListingId && !x.IsDeleted)
                .Select(x => new { x.UserId, x.IsActive }).FirstOrDefaultAsync();
            ownerId = l?.UserId; listingIsActive = l?.IsActive;
        }

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
            // Not deleted (filtered above), so isDeleted: false — genuinely reflects the
            // listing's current active/inactive state instead of always lying "Active".
            Status = ComputeLifecycleStatus(isDeleted: false, isActive: listingIsActive),
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
        Guid conversationId, DateTime? before, DateTime? after, int? limit,
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var callerId)) return UnauthorizedResponse();
        if (before.HasValue && after.HasValue) return BadRequestResponse("Pass either 'before' or 'after', not both.");

        var conversation = await unitOfWork.Conversations.GetByIdAsync(conversationId);
        if (conversation == null) return NotFoundResponse("Conversation not found");
        if (conversation.RenterId != callerId && conversation.OwnerId != callerId) return ForbiddenResponse();

        // Same reconciliation SendMessage already does, applied here too so opening a thread
        // directly (not via a fresh GetConversations page) also shows the correct lifecycle
        // status immediately rather than waiting for a failed send attempt.
        await RefreshListingLifecycleStatusAsync(conversation, db, unitOfWork);
        await unitOfWork.SaveChangesAsync();

        // 'after' is the reconnect-catch-up path (give me everything since the last message I
        // already have); 'before' is normal backward history-scrolling. Never both at once.
        var effectiveLimit = Math.Clamp(limit ?? DefaultPageSize, 1, 100);
        var messages = await unitOfWork.Messages.GetPagedForConversationAsync(
            conversationId, before, after, effectiveLimit);

        var dtos = messages.Select(m => m.ToDto(isMine: m.SenderId == callerId)).ToList();

        // A full page means there's likely more beyond it — same heuristic the
        // conversations-list endpoint's client side already uses (items.length >= pageSize).
        // Drives both "load older on scroll to top" and the reconnect catch-up loop.
        var hasMore = messages.Count == effectiveLimit;

        return OkResponse(new { items = dtos, conversationStatus = conversation.Status, hasMore });
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

        // The initial visit proposal is sent through this generic endpoint (only the
        // accept/decline/counter responses go through RespondSchedule, which has its own
        // FluentValidation future-time check) — SendMessageRequestValidator never inspects
        // payload content at all, so without this, a past-dated initial proposal was never
        // rejected anywhere.
        if (request.Type == "schedule_proposal")
        {
            var scheduleError = ValidateScheduleProposalPayload(request.PayloadJson);
            if (scheduleError != null) return BadRequestResponse(scheduleError);
        }

        // A fresh catalog question (quick_reply with no RespondsToMessageId — an ANSWER always
        // has one, see _answerQuestion vs onAskQuestion client-side) gets its template's
        // answer-options snapshotted into its own stored payload here, so rendering it later
        // never depends on a live admin-catalog lookup — see TryEmbedAnswerOptionsAsync.
        var storedPayloadJson = request.PayloadJson;
        if (request.Type == "quick_reply" && request.RespondsToMessageId is null)
        {
            storedPayloadJson = await TryEmbedAnswerOptionsAsync(request.PayloadJson, unitOfWork, cache) ?? request.PayloadJson;
        }

        var message = new Message
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            SenderId = callerId,
            Type = request.Type,
            PayloadJson = storedPayloadJson,
            RespondsToMessageId = request.RespondsToMessageId,
            ClientMessageId = request.ClientMessageId,
            CreatedAt = DateTime.UtcNow,
        };

        await unitOfWork.Messages.AddAsync(message);

        try
        {
            await unitOfWork.SaveChangesAsync();
        }
        catch (DbUpdateException) when (request.RespondsToMessageId.HasValue)
        {
            // Lost a race against a concurrent answer to the same question (double-tap on
            // a reply option) — the partial unique index on RespondsToMessageId caught it.
            // Same catch-and-return-the-winner shape as the Conversations unique-index guard.
            // The winning request already applied its own conversation update — don't apply
            // a second one here for a message that was never actually ours.
            var existingAnswer = await db.Messages.FirstOrDefaultAsync(m => m.RespondsToMessageId == request.RespondsToMessageId.Value);
            if (existingAnswer != null)
            {
                return OkResponse(existingAnswer.ToDto(isMine: existingAnswer.SenderId == callerId));
            }
            throw;
        }
        // Same shape, for a fresh (non-answer) send racing itself — a double-tap that fires
        // two requests for the same compose attempt before either resolves. Mutually exclusive
        // with the catch above by construction (a fresh send never sets RespondsToMessageId).
        catch (DbUpdateException) when (!request.RespondsToMessageId.HasValue && request.ClientMessageId.HasValue)
        {
            var existingMessage = await db.Messages.FirstOrDefaultAsync(m =>
                m.ConversationId == conversationId && m.SenderId == callerId && m.ClientMessageId == request.ClientMessageId.Value);
            if (existingMessage != null)
            {
                return OkResponse(existingMessage.ToDto(isMine: true));
            }
            throw;
        }

        // Only reached once the message is durably persisted — see ApplyToConversationAsync's
        // own comment for why this ordering matters.
        var recipientUnreadCount = await ApplyToConversationAsync(conversation, callerId, otherPartyId, message, unitOfWork, cache);
        await BroadcastMessageAsync(hubContext, conversation, message, isNewMessage: true, recipientUnreadCount: recipientUnreadCount);
        await PublishPushNotificationAsync(publisher, unitOfWork, conversation, callerId, otherPartyId, message, cache);

        return CreatedResponse(message.ToDto(isMine: true), $"/api/v1/chat/conversations/{conversationId}/messages/{message.Id}");
    }

    public static async Task<IResult> MarkRead(
        Guid conversationId, ClaimsPrincipal principal, IUnitOfWork unitOfWork, IHubContext<ChatHub> hubContext,
        IRateLimitService rateLimiter, HttpContext httpContext)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var callerId)) return UnauthorizedResponse();

        var conversation = await unitOfWork.Conversations.GetByIdAsync(conversationId);
        if (conversation == null) return NotFoundResponse("Conversation not found");
        if (conversation.RenterId != callerId && conversation.OwnerId != callerId) return ForbiddenResponse();

        var rl = await rateLimiter.CheckAsync($"chat:markread:{callerId}", MarkReadMaxPerMinute, SendMessageWindow);
        if (!rl.IsAllowed)
        {
            httpContext.Response.Headers["Retry-After"] = ((int)rl.RetryAfter!.Value.TotalSeconds).ToString();
            return TooManyRequestsResponse();
        }

        await unitOfWork.Messages.MarkReadBulkAsync(conversationId, callerId);

        // Recomputed from Messages.ReadAt (the same source of truth MarkReadBulkAsync just
        // wrote to) via one atomic ExecuteUpdateAsync, not a tracked-entity "set to 0" — that
        // used to silently lose a concurrent incoming message's increment if it landed between
        // this handler's load and save. Self-healing: a message that arrives in the gap between
        // MarkReadBulkAsync and this call is still correctly counted unread.
        var newCount = await unitOfWork.Conversations.RecomputeUnreadCountAsync(
            conversationId, callerId, conversation.RenterId == callerId);

        await hubContext.Clients.Group($"conversation_{conversationId}")
            .SendAsync("MessagesRead", new { conversationId, readByUserId = callerId });
        // The reader's OWN other devices — not the other party — need to hear about this too.
        // They're always in user_{callerId} (joined on every connection) but only join
        // conversation_{id} while that exact screen is open, so the broadcast above alone never
        // reaches a second device sitting on the Chats list. Reuses the same event name/shape
        // the recipient already gets on a new message (see BroadcastMessageAsync).
        await hubContext.Clients.Group($"user_{callerId}")
            .SendAsync("UnreadCountChanged", new { conversationId, unreadCount = newCount });

        return OkResponse(new { success = true });
    }

    // Same `{ count }` shape as NotificationInboxHandlers.GetUnreadCount so the client can
    // parse both unread-count endpoints identically.
    public static async Task<IResult> GetUnreadCount(ClaimsPrincipal principal, IUnitOfWork unitOfWork)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var callerId)) return UnauthorizedResponse();

        var count = await unitOfWork.Conversations.GetTotalUnreadForUserAsync(callerId);
        return OkResponse(new { count });
    }

    // ── Contact request/response ────────────────────────────────────────────

    public static async Task<IResult> RespondContact(
        Guid messageId, RespondContactRequest request,
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, IHubContext<ChatHub> hubContext, IMemoryCache cache,
        ApplicationDbContext db, IRabbitMqPublisher publisher, IRateLimitService rateLimiter, HttpContext httpContext)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var callerId)) return UnauthorizedResponse();

        var original = await unitOfWork.Messages.GetByIdAsync(messageId);
        if (original == null || original.Type != "contact_request") return NotFoundResponse("Contact request not found");
        if (original.SenderId == callerId) return BadRequestResponse("You can't respond to your own request");

        var conversation = await unitOfWork.Conversations.GetByIdAsync(original.ConversationId);
        if (conversation == null) return NotFoundResponse("Conversation not found");
        // Either side can send a contact request (SendMessage has no owner/renter restriction) and
        // either side can respond to one addressed to them — same generic participant check
        // RespondSchedule uses, no owner-only gate. Whoever approves shares THEIR OWN number, not
        // "the owner's" — a renter approving a request from the owner reveals the renter's number.
        if (conversation.RenterId != callerId && conversation.OwnerId != callerId) return ForbiddenResponse();
        var otherPartyId = conversation.RenterId == callerId ? conversation.OwnerId : conversation.RenterId;

        // Same two guards SendMessage already has — without these, a blocked user (or a
        // conversation whose listing has since gone inactive/removed) could still approve a
        // pending contact request, leaking their phone number, fully bypassing the block.
        await RefreshListingLifecycleStatusAsync(conversation, db, unitOfWork);
        if (conversation.Status != "Active")
        {
            await unitOfWork.SaveChangesAsync();
            return BadRequestResponse($"This conversation is no longer active ({conversation.Status})");
        }

        if (await unitOfWork.UserBlocks.ExistsAsync(otherPartyId, callerId) ||
            await unitOfWork.UserBlocks.ExistsAsync(callerId, otherPartyId))
            return ForbiddenResponse("You can't message this user");

        // Shares SendMessage's own bucket, deliberately — the abuse surface is "how much chat
        // activity can this user generate," not the specific action type. A separate bucket
        // here would just relocate the same unmetered spam vector rather than close it.
        var rl = await rateLimiter.CheckAsync($"chat:send:{callerId}", SendMessageMaxPerMinute, SendMessageWindow);
        if (!rl.IsAllowed)
        {
            httpContext.Response.Headers["Retry-After"] = ((int)rl.RetryAfter!.Value.TotalSeconds).ToString();
            return TooManyRequestsResponse();
        }

        var responder = await unitOfWork.Users.GetByIdAsync(callerId);
        if (responder == null) return NotFoundResponse("User not found");

        var payload = request.Approve
            ? JsonSerializer.Serialize(new { approved = true, phone = responder.PhoneNumber })
            : JsonSerializer.Serialize(new { approved = false });

        var response = new Message
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            SenderId = callerId,
            Type = "contact_response",
            PayloadJson = payload,
            RespondsToMessageId = messageId,
            CreatedAt = DateTime.UtcNow,
        };

        await unitOfWork.Messages.AddAsync(response);

        try
        {
            await unitOfWork.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Same race guard as RespondSchedule — see its comment.
            var existing = await db.Messages.FirstOrDefaultAsync(m => m.RespondsToMessageId == messageId);
            if (existing != null)
            {
                return OkResponse(existing.ToDto(isMine: existing.SenderId == callerId));
            }
            throw;
        }

        var recipientUnreadCount = await ApplyToConversationAsync(conversation, callerId, otherPartyId, response, unitOfWork, cache);
        await BroadcastMessageAsync(hubContext, conversation, response, isNewMessage: true, recipientUnreadCount: recipientUnreadCount);
        await PublishPushNotificationAsync(publisher, unitOfWork, conversation, callerId, otherPartyId, response, cache);

        return OkResponse(response.ToDto(isMine: true));
    }

    // ── Schedule propose/respond ────────────────────────────────────────────

    public static async Task<IResult> RespondSchedule(
        Guid messageId, RespondScheduleRequest request, IValidator<RespondScheduleRequest> validator,
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, IHubContext<ChatHub> hubContext, IMemoryCache cache,
        ApplicationDbContext db, IRabbitMqPublisher publisher, IRateLimitService rateLimiter, HttpContext httpContext)
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

        // Same two guards SendMessage/RespondContact already have — without these, a blocked
        // user (or a conversation whose listing has since gone inactive/removed) could still
        // accept/decline/counter a stale proposal, fully bypassing the block.
        await RefreshListingLifecycleStatusAsync(conversation, db, unitOfWork);
        if (conversation.Status != "Active")
        {
            await unitOfWork.SaveChangesAsync();
            return BadRequestResponse($"This conversation is no longer active ({conversation.Status})");
        }

        if (await unitOfWork.UserBlocks.ExistsAsync(otherPartyId, callerId) ||
            await unitOfWork.UserBlocks.ExistsAsync(callerId, otherPartyId))
            return ForbiddenResponse("You can't message this user");

        // Same shared bucket as SendMessage/RespondContact — see RespondContact's comment.
        // Matters most here: "counter" has no frequency limit otherwise, only a same-sender
        // guard, so two accounts could volley proposals back and forth indefinitely without this.
        var rl = await rateLimiter.CheckAsync($"chat:send:{callerId}", SendMessageMaxPerMinute, SendMessageWindow);
        if (!rl.IsAllowed)
        {
            httpContext.Response.Headers["Retry-After"] = ((int)rl.RetryAfter!.Value.TotalSeconds).ToString();
            return TooManyRequestsResponse();
        }

        if (request.Action == "counter")
        {
            // Mark the original proposal superseded — stays visible, faded, no longer actionable.
            // original.PayloadJson was written by whichever client sent the initial proposal;
            // SendMessageRequestValidator only checks NotEmpty/MaxLength, not that it's valid
            // JSON, so this must degrade gracefully rather than 500 on a malformed payload.
            // Uses TrySetJsonField (JsonNode-based) rather than a JsonDocument+Dictionary+
            // ToString() walk — the latter corrupted array-valued fields like proposedAts into
            // JSON-encoded strings on every supersede, which then threw on the client trying to
            // cast the now-stringified array back to a List.
            original.PayloadJson = TrySetJsonField(original.PayloadJson, "status", "superseded")
                ?? JsonSerializer.Serialize(new { status = "superseded" });
            await unitOfWork.Messages.UpdateAsync(original);

            var counter = new Message
            {
                Id = Guid.NewGuid(),
                ConversationId = conversation.Id,
                SenderId = callerId,
                Type = "schedule_proposal",
                PayloadJson = JsonSerializer.Serialize(new { proposedAts = request.ProposedAts, status = "pending", supersedes = messageId }),
                RespondsToMessageId = messageId,
                CreatedAt = DateTime.UtcNow,
            };
            await unitOfWork.Messages.AddAsync(counter);

            try
            {
                await unitOfWork.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                // Lost a race against a concurrent response to the same proposal (double-tap,
                // or a stale card whose actions were still on-screen after an earlier response
                // already went through) — the partial unique index on RespondsToMessageId caught
                // it. Same catch-and-return-the-winner shape SendMessage already uses for
                // quick_reply answers.
                var existing = await db.Messages.FirstOrDefaultAsync(m => m.RespondsToMessageId == messageId);
                if (existing != null)
                {
                    return OkResponse(existing.ToDto(isMine: existing.SenderId == callerId));
                }
                throw;
            }

            var counterRecipientUnreadCount = await ApplyToConversationAsync(conversation, callerId, otherPartyId, counter, unitOfWork, cache);
            await BroadcastMessageAsync(hubContext, conversation, counter, isNewMessage: true, recipientUnreadCount: counterRecipientUnreadCount);
            // The original proposal's PayloadJson was persisted as "superseded" above — without
            // this, the other party's screen kept showing it as pending/actionable until they
            // left and re-entered the conversation (forcing a REST refetch). Not a new unread
            // message, so no recipientUnreadCount needed here.
            await BroadcastMessageAsync(hubContext, conversation, original, isNewMessage: false);
            await PublishPushNotificationAsync(publisher, unitOfWork, conversation, callerId, otherPartyId, counter, cache);
            return OkResponse(counter.ToDto(isMine: true));
        }

        object responsePayload;
        if (request.Action == "accept")
        {
            // Confirm the accepted time was actually one of the originally offered
            // slots — defends against a client sending a time that was never offered.
            // Degrades gracefully (rejects rather than 500s) if the original proposal's
            // payload is malformed, same shape as the "counter" branch's parse above.
            // Compared as whole-second Unix timestamps rather than raw DateTime equality —
            // a JSON round-trip can leave DateTime.Kind as Utc on one side and Unspecified on
            // the other (System.Text.Json infers Kind from whether the source string had a
            // 'Z'/offset), and raw `==` treats those as different instants even when they
            // represent the same moment; normalizing to UTC + truncating to seconds also
            // absorbs harmless sub-second jitter from serialization.
            var offeredTimes = new List<long>();
            try
            {
                using var doc = JsonDocument.Parse(original.PayloadJson);
                if (doc.RootElement.TryGetProperty("proposedAts", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    foreach (var el in arr.EnumerateArray())
                        if (el.TryGetDateTime(out var dt))
                            offeredTimes.Add(new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)).ToUnixTimeSeconds());
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }

            var acceptedAtSeconds = new DateTimeOffset(DateTime.SpecifyKind(request.AcceptedAt!.Value, DateTimeKind.Utc)).ToUnixTimeSeconds();
            if (!offeredTimes.Contains(acceptedAtSeconds))
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
            RespondsToMessageId = messageId,
            CreatedAt = DateTime.UtcNow,
        };

        await unitOfWork.Messages.AddAsync(response);

        try
        {
            await unitOfWork.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Same race guard as the "counter" branch above — see its comment.
            var existing = await db.Messages.FirstOrDefaultAsync(m => m.RespondsToMessageId == messageId);
            if (existing != null)
            {
                return OkResponse(existing.ToDto(isMine: existing.SenderId == callerId));
            }
            throw;
        }

        var recipientUnreadCount = await ApplyToConversationAsync(conversation, callerId, otherPartyId, response, unitOfWork, cache);
        await BroadcastMessageAsync(hubContext, conversation, response, isNewMessage: true, recipientUnreadCount: recipientUnreadCount);
        await PublishPushNotificationAsync(publisher, unitOfWork, conversation, callerId, otherPartyId, response, cache);
        return OkResponse(response.ToDto(isMine: true));
    }

    // ── Block ────────────────────────────────────────────────────────────────

    public static async Task<IResult> BlockUser(
        Guid userId, ClaimsPrincipal principal, IUnitOfWork unitOfWork, IHubContext<ChatHub> hubContext)
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
        var flipped = new List<Conversation>();
        foreach (var conversation in conversations)
        {
            if (conversation.Status == "Blocked") continue;
            conversation.Status = "Blocked";
            await unitOfWork.Conversations.UpdateAsync(conversation);
            flipped.Add(conversation);
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
            // this file (e.g. CreateConversation's unique-index race). Skip the push below too:
            // the winning request already pushed for the same end state.
            return NoContentResponse();
        }

        // Live-push to both sides — the OTHER party's open conversation screen (if any) and
        // their Chats list, mirroring BroadcastMessageAsync's own two-group pattern below.
        // blockedByUserId (the blocker's own id, always `callerId` here) lets EACH recipient
        // independently derive their own "did I block them, or did they block me" by comparing
        // it against their own logged-in user id client-side — Status alone is a symmetric
        // string with no sense of direction.
        foreach (var conversation in flipped)
        {
            await hubContext.Clients.Group($"conversation_{conversation.Id}")
                .SendAsync("ConversationStatusChanged", new { conversationId = conversation.Id, status = conversation.Status, blockedByUserId = callerId });
            await hubContext.Clients.Group($"user_{userId}")
                .SendAsync("ConversationStatusChanged", new { conversationId = conversation.Id, status = conversation.Status, blockedByUserId = callerId });
        }
        return NoContentResponse();
    }

    public static async Task<IResult> UnblockUser(
        Guid userId, ClaimsPrincipal principal, IUnitOfWork unitOfWork, IHubContext<ChatHub> hubContext)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var callerId)) return UnauthorizedResponse();
        if (callerId == userId) return BadRequestResponse("You can't unblock yourself");

        var existing = await unitOfWork.UserBlocks.GetByBlockerAndBlockedAsync(callerId, userId);
        if (existing == null) return NoContentResponse(); // caller never blocked this user — nothing to undo

        await unitOfWork.UserBlocks.DeleteAsync(existing);

        // Mutual blocks are possible (UserBlocks has no symmetry constraint — A and B can each
        // independently block the other), so clearing MY block doesn't necessarily mean the
        // conversation should reopen: if the other party still has ME blocked, restoring
        // "Active" here would desync the UI (composer re-enables) from SendMessage's own block
        // check (still rejects) — confusing, not just cosmetically wrong.
        var stillBlockedByOtherParty = await unitOfWork.UserBlocks.ExistsAsync(userId, callerId);

        // Deliberate user action — unlike the listing-lifecycle reconciler (which
        // intentionally never overrides a "Blocked" status automatically), an explicit
        // unblock is exactly the case that should restore it. If the listing has since
        // gone inactive/removed, the next SendMessage/GetConversations reconciliation
        // pass will correct it from here, same as it would for any other conversation.
        // Only reaches here when `existing` was real — a caller who never blocked this
        // user (e.g. the other party trying to clear a block they don't own) can't flip
        // Status back just by hitting this endpoint.
        var conversations = await unitOfWork.Conversations.GetAllBetweenUsersAsync(callerId, userId);
        var flipped = new List<Conversation>();
        foreach (var conversation in conversations)
        {
            if (conversation.Status != "Blocked") continue;
            if (stillBlockedByOtherParty) continue;
            conversation.Status = "Active";
            await unitOfWork.Conversations.UpdateAsync(conversation);
            flipped.Add(conversation);
        }

        await unitOfWork.SaveChangesAsync();

        foreach (var conversation in flipped)
        {
            await hubContext.Clients.Group($"conversation_{conversation.Id}")
                .SendAsync("ConversationStatusChanged", new { conversationId = conversation.Id, status = conversation.Status });
            await hubContext.Clients.Group($"user_{userId}")
                .SendAsync("ConversationStatusChanged", new { conversationId = conversation.Id, status = conversation.Status });
        }
        return NoContentResponse();
    }

    // ── Question templates (shared GET — also mapped under /admin) ─────────

    // Shared handler (mapped under both the public /chat/... and /admin/... routes) — the
    // cache always holds the admin-complete list (including inactive templates); filtering
    // happens here at the response boundary based on caller identity, not by maintaining a
    // second cache. Only an admin session (the same actor_type claim value the "AdminOnly"
    // policy itself checks, see AuthenticationExtensions.cs) sees inactive templates — every
    // other caller only sees active ones, closing both the info-exposure (deactivated question
    // text was visible in the raw response to any authenticated renter) and the timing gap
    // (a stale client could otherwise still successfully submit a just-deactivated question).
    public static async Task<IResult> GetQuestionTemplates(IUnitOfWork unitOfWork, IMemoryCache cache, ClaimsPrincipal principal)
    {
        var templates = await GetCachedQuestionTemplatesAsync(unitOfWork, cache);
        if (principal.HasClaim("actor_type", "admin")) return OkResponse(templates);
        return OkResponse(templates.Where(t => t.IsActive).ToList());
    }

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
        try
        {
            await unitOfWork.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Key is DB-unique (ApplicationDbContext.cs) but nothing pre-checks it above —
            // a race or a simple duplicate-typo both land here as a friendly 409 instead of
            // falling through to the generic unhandled-exception 500.
            return ConflictResponse("A question with this key already exists");
        }

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

    private readonly record struct ListingInfoLite(string Title, string? Photo, Guid? RoomTypeId, Guid? PlotTypeId, bool? IsDeleted, bool? IsActive, string? Area);

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
        string? area;

        if (listingInfo != null && listingInfo.TryGetValue((c.ListingType, c.ListingId), out var info))
        {
            listingTitle = info.Title;
            thumbnailUrl = info.Photo;
            roomTypeId = info.RoomTypeId;
            plotTypeId = info.PlotTypeId;
            area = info.Area;
        }
        else if (c.ListingType == "Room")
        {
            var l = await db.RoomListings.Where(x => x.Id == c.ListingId)
                .Select(x => new {
                    x.RoomType.Name, x.RoomTypeId,
                    Photo = x.Photos.OrderBy(p => p.PhotoOrder).Select(p => p.PhotoUrl).FirstOrDefault(),
                    CityName = x.City != null ? x.City.Name : null,
                    DistrictName = x.District.Name,
                })
                .FirstOrDefaultAsync();
            listingTitle = l?.Name ?? "Room";
            thumbnailUrl = l?.Photo;
            roomTypeId = l?.RoomTypeId;
            area = l?.CityName ?? l?.DistrictName;
        }
        else
        {
            var l = await db.PlotListings.Where(x => x.Id == c.ListingId)
                .Select(x => new {
                    x.PlotType.Name, x.PlotTypeId,
                    Photo = x.Photos.OrderBy(p => p.PhotoOrder).Select(p => p.PhotoUrl).FirstOrDefault(),
                    CityName = x.City != null ? x.City.Name : null,
                    DistrictName = x.District.Name,
                })
                .FirstOrDefaultAsync();
            listingTitle = l?.Name ?? "Plot";
            thumbnailUrl = l?.Photo;
            plotTypeId = l?.PlotTypeId;
            area = l?.CityName ?? l?.DistrictName;
        }

        // Only meaningful (and only worth a query) when the conversation is actually Blocked —
        // c.Status alone can't say WHO blocked whom (UserBlocks(BlockerId, BlockedId) is the
        // real source of truth, not a second field bolted onto Conversation). Skipped for the
        // common non-Blocked case to avoid a query on every row of a paginated list.
        var isBlockedByMe = c.Status == "Blocked" &&
            await db.UserBlocks.AnyAsync(b => b.BlockerId == callerId && b.BlockedId == otherPartyId);

        return new ConversationDto
        {
            Id = c.Id,
            ListingType = c.ListingType,
            ListingId = c.ListingId,
            RoomTypeId = roomTypeId,
            PlotTypeId = plotTypeId,
            ListingTitle = listingTitle,
            Area = area,
            ListingThumbnailUrl = thumbnailUrl,
            OtherPartyId = otherPartyId,
            OtherPartyName = otherPartyName,
            IsOwner = isOwner,
            Status = c.Status,
            IsBlockedByMe = isBlockedByMe,
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
                .Select(l => new {
                    l.Id, TypeName = l.RoomType.Name, l.RoomTypeId, l.IsDeleted, l.IsActive,
                    Photo = l.Photos.OrderBy(p => p.PhotoOrder).Select(p => p.PhotoUrl).FirstOrDefault(),
                    // Same CityName-then-DistrictName precedence as ListingDto/PlotDto's mapping.
                    CityName = l.City != null ? l.City.Name : null,
                    DistrictName = l.District.Name,
                })
                .ToListAsync();
            foreach (var r in rows)
                result[("Room", r.Id)] = new ListingInfoLite(r.TypeName ?? "Room", r.Photo, r.RoomTypeId, null, r.IsDeleted, r.IsActive, r.CityName ?? r.DistrictName);
        }

        var plotIds = conversations.Where(c => c.ListingType == "Plot").Select(c => c.ListingId).Distinct().ToList();
        if (plotIds.Count > 0)
        {
            var rows = await db.PlotListings.Where(l => plotIds.Contains(l.Id))
                .Select(l => new {
                    l.Id, TypeName = l.PlotType.Name, l.PlotTypeId, l.IsDeleted, l.IsActive,
                    Photo = l.Photos.OrderBy(p => p.PhotoOrder).Select(p => p.PhotoUrl).FirstOrDefault(),
                    CityName = l.City != null ? l.City.Name : null,
                    DistrictName = l.District.Name,
                })
                .ToListAsync();
            foreach (var r in rows)
                result[("Plot", r.Id)] = new ListingInfoLite(r.TypeName ?? "Plot", r.Photo, null, r.PlotTypeId, r.IsDeleted, r.IsActive, r.CityName ?? r.DistrictName);
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
    // against Messages needed for the common case. Atomic ExecuteUpdateAsync, not a
    // tracked-entity mutation — the old tracked write let any concurrent writer to the same
    // Conversation row (a mark-read, another message, a block/unblock) clobber this one's
    // change on every field, not just the unread counter. Callers must invoke this only AFTER
    // the message itself is durably saved (never staged alongside it on the same
    // SaveChangesAsync, since this runs immediately) — that ordering is what keeps
    // LastMessagePreview/the unread bump from ever pointing at a message that didn't actually
    // make it in, without needing a wrapping transaction. Returns the recipient's fresh unread
    // count for the caller to broadcast live.
    private static async Task<int> ApplyToConversationAsync(Conversation conversation, Guid senderId, Guid recipientId, Message message, IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        var preview = await BuildPreviewAsync(message, unitOfWork, cache);
        var recipientIsRenter = recipientId == conversation.RenterId;
        return await unitOfWork.Conversations.ApplyIncomingMessageAsync(conversation.Id, message.CreatedAt, preview, recipientIsRenter);
    }

    // Parses payloadJson as a JSON object and sets `key` to `value`, preserving every other
    // field's original type (arrays stay arrays, numbers stay numbers, etc.) — this is what
    // both RespondSchedule's "counter" branch and SendMessage's quick_reply answer-options
    // enrichment need, instead of the old JsonDocument+Dictionary<string,object?> approach
    // whose .ToString()-per-property step silently re-encoded array/object fields as
    // JSON-in-a-string on save (this is exactly what previously corrupted proposedAts).
    // Returns null if payloadJson isn't a parseable JSON object — callers pick their own
    // fallback, since "malformed original payload" means something different to each caller
    // (RespondSchedule wants a fresh replacement object; SendMessage's enrichment wants to
    // leave the client's own payload completely untouched rather than risk losing it).
    private static string? TrySetJsonField(string payloadJson, string key, JsonNode? value)
    {
        try
        {
            var obj = JsonNode.Parse(payloadJson)?.AsObject();
            if (obj == null) return null;
            obj[key] = value;
            return obj.ToJsonString();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    // Guards the INITIAL visit proposal (sent via the generic SendMessage endpoint, type
    // "schedule_proposal") — the only schedule-related payload SendMessageRequestValidator
    // never inspects. Mirrors RespondScheduleRequestValidator's own future-time check (same
    // 1-minute grace window for client/server clock and network latency) so a past-dated visit
    // can't be proposed from a modified client or a direct API call either.
    private static string? ValidateScheduleProposalPayload(string payloadJson)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(payloadJson);
        }
        catch (JsonException)
        {
            return "Invalid schedule proposal payload";
        }

        var proposedAts = node?["proposedAts"]?.AsArray();
        if (proposedAts == null || proposedAts.Count == 0)
            return "proposedAts must include at least one time";

        foreach (var entry in proposedAts)
        {
            if (entry == null || !entry.AsValue().TryGetValue<DateTime>(out var dt))
                return "proposedAts must contain valid dates";
            if (dt.ToUniversalTime() <= DateTime.UtcNow.AddMinutes(-1))
                return "proposedAts must all be in the future";
        }

        return null;
    }

    // Only for a fresh quick_reply QUESTION (RespondsToMessageId == null) — an ANSWER's
    // payload (the tapped option's own key/text) has no template-level answerOptions to
    // embed. Snapshots the template's current AnswerOptionsJson into the stored message
    // payload at send time so this specific question renders identically forever after,
    // independent of the template being edited/deactivated by an admin later. Reuses the
    // same "question_templates" cache entry GetCachedQuestionTemplatesAsync already
    // maintains — no extra uncached DB query.
    private static async Task<string?> TryEmbedAnswerOptionsAsync(string payloadJson, IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        string? key;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (!doc.RootElement.TryGetProperty("key", out var keyEl)) return null;
            key = keyEl.GetString();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
        if (string.IsNullOrEmpty(key)) return null;

        var templates = await GetCachedQuestionTemplatesAsync(unitOfWork, cache);
        // IsActive check closes a race: a stale client whose "+" menu was fetched before an
        // admin deactivated this exact template could otherwise still successfully embed its
        // answer options into a brand-new message. A deactivated template's key now behaves
        // exactly like an unrecognized one below.
        var template = templates.FirstOrDefault(t => t.Key == key && t.IsActive);
        // Stale client / template deleted or deactivated between fetch and send — send the
        // question through unenriched rather than failing it, matching today's graceful
        // "no options render" shape.
        if (template == null) return null;

        JsonNode? answerOptionsNode;
        try
        {
            answerOptionsNode = JsonNode.Parse(template.AnswerOptionsJson);
        }
        catch (JsonException)
        {
            return null;
        }

        return TrySetJsonField(payloadJson, "answerOptions", answerOptionsNode);
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
                        // The client always sends the human-readable text alongside the key —
                        // for a QUESTION it's the template's own QuestionText, for an ANSWER
                        // it's the tapped reply option's text — so reading it directly here
                        // covers both without needing any lookup. This also fixes a real bug:
                        // an answer's key (e.g. "yes_available") belongs to a different
                        // namespace than a question template's own Key (e.g. "is_available"),
                        // so the old key-matching-only lookup below always missed answers and
                        // fell through to "New message" for the (very common) case of the last
                        // message in a conversation being a reply rather than a fresh question.
                        if (doc.RootElement.TryGetProperty("text", out var textEl) &&
                            textEl.GetString() is { Length: > 0 } text)
                            return text;

                        // Fallback for any older/malformed payload missing "text" — matches
                        // only a question's own key, not an answer's, but better than nothing.
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

    // The one shared path every message-mutating handler must go through to stay live for both
    // parties — new messages (isNewMessage: true) push MessageReceived + bump the recipient's
    // unread count; an existing message merely changing state (e.g. a schedule proposal marked
    // "superseded") pushes MessageUpdated to the conversation only, no unread bump since it isn't
    // new unread content. Funneling both through one method is what makes forgetting to broadcast
    // a state mutation (as the schedule-counter branch below used to) structurally harder to repeat.
    // recipientUnreadCount is only meaningful (and only read) when isNewMessage is true — the
    // caller must pass the fresh count ApplyToConversationAsync/ApplyIncomingMessageAsync
    // returned, since the in-memory `conversation` object's own counter fields are stale the
    // instant that atomic ExecuteUpdateAsync runs (it bypasses the change tracker entirely).
    private static async Task BroadcastMessageAsync(IHubContext<ChatHub> hubContext, Conversation conversation, Message message, bool isNewMessage, int recipientUnreadCount = 0)
    {
        // recipient's client compares SenderId against its own userId itself
        var dto = message.ToDto(isMine: false);
        var eventName = isNewMessage ? "MessageReceived" : "MessageUpdated";
        await hubContext.Clients.Group($"conversation_{conversation.Id}").SendAsync(eventName, dto);

        if (!isNewMessage) return;

        var recipientId = message.SenderId == conversation.RenterId ? conversation.OwnerId : conversation.RenterId;
        await hubContext.Clients.Group($"user_{recipientId}")
            .SendAsync("UnreadCountChanged", new { conversationId = conversation.Id, unreadCount = recipientUnreadCount });
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
