using RentNearBy.Core.Models;

namespace RentNearBy.Core.Interfaces;

public sealed record FcmMessageSpec(string Token, string Title, string Body, IReadOnlyDictionary<string, string> Data, string? Tag = null);

public enum FcmSendOutcome { Sent, InvalidToken, Failed }

public interface IFcmService
{
    Task<bool> SendAsync(string token, string title, string body, string notificationType, IDictionary<string, string>? data = null);
    Task<bool> SendAsync(string token, string title, string body, string notificationType, NotificationDestination destination);
    Task<bool> SendToTopicAsync(string topic, string title, string body, IDictionary<string, string>? data = null);

    // One call per up to 500 messages (FCM v1 has no multicast; the SDK sends them concurrently). One outcome per message, same order.
    Task<IReadOnlyList<FcmSendOutcome>> SendEachAsync(IReadOnlyList<FcmMessageSpec> messages, CancellationToken ct = default);
}
