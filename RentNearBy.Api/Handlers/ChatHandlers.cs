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

        var dtos = new List<ConversationDto>(conversations.Count);
        foreach (var c in conversations)
            dtos.Add(await BuildConversationDtoAsync(c, callerId, db));

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
        if (existing != null) return OkResponse(await BuildConversationDtoAsync(existing, callerId, db));

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
        await unitOfWork.SaveChangesAsync();

        return CreatedResponse(await BuildConversationDtoAsync(conversation, callerId, db), $"/api/v1/chat/conversations/{conversation.Id}");
    }

    // ── Messages ─────────────────────────────────────────────────────────────

    public static async Task<IResult> GetMessages(
        Guid conversationId, DateTime? before, int? limit,
        ClaimsPrincipal principal, IUnitOfWork unitOfWork)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var callerId)) return UnauthorizedResponse();

        var conversation = await unitOfWork.Conversations.GetByIdAsync(conversationId);
        if (conversation == null) return NotFoundResponse("Conversation not found");
        if (conversation.RenterId != callerId && conversation.OwnerId != callerId) return ForbiddenResponse();

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
            ReadAt = m.ReadAt,
            CreatedAt = m.CreatedAt,
        }).ToList();

        return OkResponse(new { items = dtos });
    }

    public static async Task<IResult> SendMessage(
        Guid conversationId, SendMessageRequest request, IValidator<SendMessageRequest> validator,
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, IRateLimitService rateLimiter,
        IHubContext<ChatHub> hubContext, IRabbitMqPublisher publisher,
        ApplicationDbContext db, HttpContext httpContext)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        if (!UsersHandlers.TryGetUserId(principal, out var callerId)) return UnauthorizedResponse();

        var conversation = await unitOfWork.Conversations.GetByIdAsync(conversationId);
        if (conversation == null) return NotFoundResponse("Conversation not found");
        if (conversation.RenterId != callerId && conversation.OwnerId != callerId) return ForbiddenResponse();

        await RefreshListingLifecycleStatusAsync(conversation, db, unitOfWork);
        if (conversation.Status != "Active")
            return BadRequestResponse($"This conversation is no longer active ({conversation.Status})");

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

        var message = new Message
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            SenderId = callerId,
            Type = request.Type,
            PayloadJson = request.PayloadJson,
            CreatedAt = DateTime.UtcNow,
        };

        await unitOfWork.Messages.AddAsync(message);
        await ApplyToConversationAsync(conversation, callerId, otherPartyId, message, unitOfWork);
        await unitOfWork.SaveChangesAsync();

        await PushMessageAsync(hubContext, conversation, message);
        await PublishPushNotificationAsync(publisher, unitOfWork, conversation, callerId, otherPartyId, message);

        return CreatedResponse(new MessageDto
        {
            Id = message.Id, ConversationId = conversationId, SenderId = callerId, IsMine = true,
            Type = message.Type, PayloadJson = message.PayloadJson, CreatedAt = message.CreatedAt,
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
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, IHubContext<ChatHub> hubContext)
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
        await ApplyToConversationAsync(conversation, callerId, conversation.RenterId, response, unitOfWork);
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
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, IHubContext<ChatHub> hubContext)
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
            using (var doc = JsonDocument.Parse(original.PayloadJson))
            {
                var dict = doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.ToString());
                dict["status"] = "superseded";
                original.PayloadJson = JsonSerializer.Serialize(dict);
            }
            await unitOfWork.Messages.UpdateAsync(original);

            var counter = new Message
            {
                Id = Guid.NewGuid(),
                ConversationId = conversation.Id,
                SenderId = callerId,
                Type = "schedule_proposal",
                PayloadJson = JsonSerializer.Serialize(new { proposedAt = request.ProposedAt, status = "pending", supersedes = messageId }),
                CreatedAt = DateTime.UtcNow,
            };
            await unitOfWork.Messages.AddAsync(counter);
            await ApplyToConversationAsync(conversation, callerId, otherPartyId, counter, unitOfWork);
            await unitOfWork.SaveChangesAsync();

            await PushMessageAsync(hubContext, conversation, counter);
            return OkResponse(new MessageDto
            {
                Id = counter.Id, ConversationId = conversation.Id, SenderId = callerId, IsMine = true,
                Type = counter.Type, PayloadJson = counter.PayloadJson, CreatedAt = counter.CreatedAt,
            });
        }

        var status = request.Action == "accept" ? "accepted" : "declined";
        var response = new Message
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            SenderId = callerId,
            Type = "schedule_response",
            PayloadJson = JsonSerializer.Serialize(new { status, respondsTo = messageId }),
            CreatedAt = DateTime.UtcNow,
        };

        await unitOfWork.Messages.AddAsync(response);
        await ApplyToConversationAsync(conversation, callerId, otherPartyId, response, unitOfWork);
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
            await unitOfWork.SaveChangesAsync();
        }

        return NoContentResponse();
    }

    // ── Question templates (shared GET — also mapped under /admin) ─────────

    public static async Task<IResult> GetQuestionTemplates(IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        if (!cache.TryGetValue("question_templates", out List<QuestionTemplateDto>? cached) || cached == null)
        {
            var templates = await unitOfWork.QuestionTemplates.GetAllAsync();
            cached = templates.OrderBy(t => t.SortOrder).Select(t => t.Adapt<QuestionTemplateDto>()).ToList();
            cache.Set("question_templates", cached, CacheTtl);
        }
        return OkResponse(cached);
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private static async Task<ConversationDto> BuildConversationDtoAsync(Conversation c, Guid callerId, ApplicationDbContext db)
    {
        var isOwner = c.OwnerId == callerId;
        var otherPartyId = isOwner ? c.RenterId : c.OwnerId;
        var otherPartyName = await db.Users.Where(u => u.Id == otherPartyId).Select(u => u.Name).FirstOrDefaultAsync() ?? "User";

        string listingTitle;
        string? thumbnailUrl;
        if (c.ListingType == "Room")
        {
            var info = await db.RoomListings.Where(l => l.Id == c.ListingId)
                .Select(l => new { l.RoomType.Name, Photo = l.Photos.OrderBy(p => p.PhotoOrder).Select(p => p.PhotoUrl).FirstOrDefault() })
                .FirstOrDefaultAsync();
            listingTitle = info?.Name ?? "Room";
            thumbnailUrl = info?.Photo;
        }
        else
        {
            var info = await db.PlotListings.Where(l => l.Id == c.ListingId)
                .Select(l => new { l.PlotType.Name, Photo = l.Photos.OrderBy(p => p.PhotoOrder).Select(p => p.PhotoUrl).FirstOrDefault() })
                .FirstOrDefaultAsync();
            listingTitle = info?.Name ?? "Plot";
            thumbnailUrl = info?.Photo;
        }

        return new ConversationDto
        {
            Id = c.Id,
            ListingType = c.ListingType,
            ListingId = c.ListingId,
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

    // Denormalized fields updated in lockstep with every message — keeps the
    // Chats-list screen a single indexed query against Conversations, no join
    // against Messages needed for the common case.
    private static async Task ApplyToConversationAsync(Conversation conversation, Guid senderId, Guid recipientId, Message message, IUnitOfWork unitOfWork)
    {
        conversation.LastMessageAt = message.CreatedAt;
        conversation.LastMessagePreview = await BuildPreviewAsync(message, unitOfWork);

        if (recipientId == conversation.RenterId) conversation.UnreadCountForRenter++;
        else conversation.UnreadCountForOwner++;

        await unitOfWork.Conversations.UpdateAsync(conversation);
    }

    private static async Task<string> BuildPreviewAsync(Message message, IUnitOfWork unitOfWork)
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
                            var templates = await unitOfWork.QuestionTemplates.GetAllAsync();
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
        Guid senderId, Guid recipientId, Message message)
    {
        try
        {
            var sender = await unitOfWork.Users.GetByIdAsync(senderId);
            var preview = await BuildPreviewAsync(message, unitOfWork);
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

        var desired = isDeleted is null or true ? "ListingRemoved" : isActive == false ? "ListingInactive" : "Active";
        if (conversation.Status != desired)
        {
            conversation.Status = desired;
            await unitOfWork.Conversations.UpdateAsync(conversation);
        }
    }
}
