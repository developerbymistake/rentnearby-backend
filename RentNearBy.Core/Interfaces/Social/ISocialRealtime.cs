namespace RentNearBy.Core.Interfaces.Social;

// One hub event for one user (always sent to the user's dm_{userId} group of the ChatHub).
public sealed record SocialHubEvent(Guid UserId, string Event, object Payload);

// Sends social chat events after commit. Best effort, never throws.
public interface ISocialRealtime
{
    Task SendAsync(IReadOnlyCollection<SocialHubEvent> events);
}
