namespace RentNearBy.Core.Interfaces.Social;

// Spec 23.9: the 14-day name rule for every user-side name change of an account with a traveller profile.
public interface INameChangeGate
{
    // Call only for a real change (a different name). Stamps the change and returns null when it is allowed (or the account has
    // no traveller profile); otherwise writes nothing and returns when the name may change again (UTC).
    Task<DateTime?> TryStampAsync(Guid userId, CancellationToken ct = default);
}
