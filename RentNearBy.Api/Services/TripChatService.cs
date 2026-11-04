using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Api.Handlers;
using RentNearBy.Api.Hubs;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Api.Services;

public sealed class TripChatService(
    IUnitOfWork unitOfWork, ApplicationDbContext db, IHubContext<ChatHub> hub, ILogger<TripChatService> logger) : ITripChatService
{
    private const int PreviewMax = 140;
    private const int CardLookback = 30;
    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);

    public async Task<TripChatPost> PostAsync(Conversation c, Guid senderId, string type, object payload, string preview)
    {
        var now = DateTime.UtcNow;
        var message = new Message
        {
            Id = Guid.NewGuid(),
            ConversationId = c.Id,
            SenderId = senderId,
            Type = type,
            PayloadJson = JsonSerializer.Serialize(payload, PayloadOptions),
            ClientMessageId = null,
            RespondsToMessageId = null,
            CreatedAt = now,
        };
        await unitOfWork.Messages.AddAsync(message);
        await unitOfWork.SaveChangesAsync();

        var shortPreview = preview.Length > PreviewMax ? preview[..PreviewMax] : preview;
        var unread = 0;
        if (senderId == c.RenterId)
            unread = await unitOfWork.Conversations.ApplyIncomingMessageAsync(c.Id, now, shortPreview, recipientIsRenter: false);
        else if (senderId == c.OwnerId)
            unread = await unitOfWork.Conversations.ApplyIncomingMessageAsync(c.Id, now, shortPreview, recipientIsRenter: true);
        else
        {
            // Actor is not a party (a deleted account's closing line): both sides see it as unread.
            unread = await unitOfWork.Conversations.ApplyIncomingMessageAsync(c.Id, now, shortPreview, recipientIsRenter: true);
            await unitOfWork.Conversations.ApplyIncomingMessageAsync(c.Id, now, shortPreview, recipientIsRenter: false);
        }

        return new TripChatPost(c, message, unread, IsNew: true);
    }

    public Task<TripChatPost> PostSystemAsync(Conversation c, Guid actorUserId, string kind, string text) =>
        PostAsync(c, actorUserId, "system", new { text, kind }, text);

    public async Task<TripChatPost?> RewriteStatusAsync(Guid messageId, string status)
    {
        var message = await unitOfWork.Messages.GetByIdAsync(messageId);
        if (message == null) return null;
        var conversation = await unitOfWork.Conversations.GetByIdAsync(message.ConversationId);
        if (conversation == null) return null;

        var rewritten = ChatHandlers.TrySetJsonField(message.PayloadJson, "status", status);
        if (rewritten == null) return new TripChatPost(conversation, message, 0, IsNew: false);

        message.PayloadJson = rewritten;
        await unitOfWork.Messages.UpdateAsync(message);
        await unitOfWork.SaveChangesAsync();
        return new TripChatPost(conversation, message, 0, IsNew: false);
    }

    public async Task<Guid?> FindCardMessageIdAsync(Guid conversationId, string type, Guid enquiryId)
    {
        var rows = await db.Messages.AsNoTracking()
            .Where(m => m.ConversationId == conversationId && m.Type == type)
            .OrderByDescending(m => m.CreatedAt)
            .Take(CardLookback)
            .Select(m => new { m.Id, m.PayloadJson })
            .ToListAsync();

        var wanted = enquiryId.ToString();
        foreach (var row in rows)
        {
            try
            {
                using var doc = JsonDocument.Parse(row.PayloadJson);
                if (doc.RootElement.TryGetProperty("enquiryId", out var id) &&
                    string.Equals(id.GetString(), wanted, StringComparison.OrdinalIgnoreCase))
                    return row.Id;
            }
            catch (JsonException) { }
        }
        return null;
    }

    public async Task AfterCommitAsync(IEnumerable<TripChatPost> posts)
    {
        foreach (var post in posts)
        {
            try
            {
                await ChatHandlers.BroadcastMessageAsync(hub, post.Conversation, post.Message, post.IsNew, post.RecipientUnread);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "trip.chat.broadcast_failed {MessageId}", post.Message.Id);
            }
        }
    }
}
