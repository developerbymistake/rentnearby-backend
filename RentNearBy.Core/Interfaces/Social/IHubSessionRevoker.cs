namespace RentNearBy.Core.Interfaces.Social;

// Ends the live hub sessions of a user (logout, role change, admin pause, account deletion) after commit. Best effort.
public interface IHubSessionRevoker
{
    Task RevokeAsync(Guid userId, string reason);
}
