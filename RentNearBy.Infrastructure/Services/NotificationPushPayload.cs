namespace RentNearBy.Infrastructure.Services;

// TargetTokens (D-14 / FB-23): the user's tokens copied before a role change deleted them; null = look them up at delivery.
public record NotificationPushPayload(Guid NotificationId, Guid UserId, IReadOnlyList<string>? TargetTokens = null);
