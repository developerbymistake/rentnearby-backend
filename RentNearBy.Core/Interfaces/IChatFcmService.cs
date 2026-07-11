namespace RentNearBy.Core.Interfaces;

// Deliberately separate from IFcmService — that interface is owned by the
// membership-expiry notification feature and is not generic (see FcmService.SendAsync's
// hardcoded "membership_type" data key). Chat needs its own data payload shape
// (conversationId, for deep-linking), so it gets its own independent service instead
// of a new overload bolted onto the existing one.
public interface IChatFcmService
{
    Task<bool> SendAsync(string token, string title, string body, Guid conversationId);
}
