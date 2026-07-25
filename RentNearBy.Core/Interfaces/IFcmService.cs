using RentNearBy.Core.Models;

namespace RentNearBy.Core.Interfaces;

public interface IFcmService
{
    Task<bool> SendAsync(string token, string title, string body, string notificationType, IDictionary<string, string>? data = null);
    Task<bool> SendAsync(string token, string title, string body, string notificationType, NotificationDestination destination);
    Task<bool> SendToTopicAsync(string topic, string title, string body, IDictionary<string, string>? data = null);
}
