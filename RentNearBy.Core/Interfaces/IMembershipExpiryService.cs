namespace RentNearBy.Core.Interfaces;

/// <summary>
/// Interface for membership expiry background service.
/// Handles automatic disabling of expired listings and memberships.
/// </summary>
public interface IMembershipExpiryService
{
    /// <summary>
    /// Processes expired memberships and disables associated listings.
    /// Called daily at 12:00 AM UTC.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation</param>
    /// <returns>Task representing the asynchronous operation</returns>
    Task ProcessMembershipExpiryAsync(CancellationToken cancellationToken);
}
