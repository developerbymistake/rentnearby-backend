using RentNearBy.Api.Handlers.Buddy.Be4;

namespace RentNearBy.Api.Endpoints;

// N1, N2 under /api/v1/notifications (not tab-gated). The old inbox routes stay in NotificationEndpoints.
public static class NoticeEndpoints
{
    public static RouteGroupBuilder MapNoticeEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/notices", NoticeHandlers.List).RequireAuthorization();
        group.MapPut("/notices/{id:guid}/read", NoticeHandlers.MarkRead).RequireAuthorization();
        return group;
    }
}
