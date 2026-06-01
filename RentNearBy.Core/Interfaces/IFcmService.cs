namespace RentNearBy.Core.Interfaces;

public interface IFcmService
{
    Task<bool> SendAsync(string token, string title, string body, string membershipType);
}
