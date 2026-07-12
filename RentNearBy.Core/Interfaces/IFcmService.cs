namespace RentNearBy.Core.Interfaces;

public interface IFcmService
{
    Task<bool> SendAsync(string token, string title, string body, string membershipType, IDictionary<string, string>? data = null);
    Task<bool> SendToTopicAsync(string topic, string title, string body, IDictionary<string, string>? data = null);
}
