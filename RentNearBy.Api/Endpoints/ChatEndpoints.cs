using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

public static class ChatEndpoints
{
    public static RouteGroupBuilder MapChatEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/conversations", ChatHandlers.GetConversations).RequireAuthorization();
        group.MapPost("/conversations", ChatHandlers.CreateConversation).RequireAuthorization();
        group.MapGet("/conversations/{conversationId:guid}/messages", ChatHandlers.GetMessages).RequireAuthorization();
        group.MapPost("/conversations/{conversationId:guid}/messages", ChatHandlers.SendMessage).RequireAuthorization();
        group.MapPost("/conversations/{conversationId:guid}/read", ChatHandlers.MarkRead).RequireAuthorization();

        group.MapPost("/messages/{messageId:guid}/contact-response", ChatHandlers.RespondContact).RequireAuthorization();
        group.MapPost("/messages/{messageId:guid}/schedule-response", ChatHandlers.RespondSchedule).RequireAuthorization();

        group.MapPost("/users/{userId:guid}/block", ChatHandlers.BlockUser).RequireAuthorization();

        group.MapGet("/question-templates", ChatHandlers.GetQuestionTemplates).RequireAuthorization();

        return group;
    }
}
