using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.Interfaces;

namespace RentNearBy.Infrastructure.Services;

public class FcmService : IFcmService
{
    private readonly ILogger<FcmService> _logger;

    public FcmService(IConfiguration configuration, ILogger<FcmService> logger)
    {
        _logger = logger;

        if (FirebaseApp.DefaultInstance != null)
            return;

        var serviceAccountJson = configuration["FCM_SERVICE_ACCOUNT_JSON"]
            ?? throw new InvalidOperationException("FCM_SERVICE_ACCOUNT_JSON not configured");

        FirebaseApp.Create(new AppOptions
        {
            Credential = GoogleCredential
                .FromJson(serviceAccountJson)
                .CreateScoped("https://www.googleapis.com/auth/firebase.messaging")
        });
    }

    public async Task<bool> SendAsync(string token, string title, string body, string notificationType, IDictionary<string, string>? data = null)
    {
        var payload = new Dictionary<string, string> { { "notification_type", notificationType } };
        if (data != null)
        {
            foreach (var kv in data) payload[kv.Key] = kv.Value;
        }

        var message = new Message
        {
            Token = token,
            Notification = new Notification
            {
                Title = title,
                Body = body
            },
            Data = payload,
            Android = new AndroidConfig
            {
                Priority = Priority.High,
                Notification = new AndroidNotification
                {
                    Sound = "default",
                    ClickAction = "FLUTTER_NOTIFICATION_CLICK"
                }
            }
        };

        try
        {
            await FirebaseMessaging.DefaultInstance.SendAsync(message);
            return true;
        }
        catch (FirebaseMessagingException ex)
            when (ex.MessagingErrorCode == MessagingErrorCode.Unregistered)
        {
            _logger.LogWarning("FCM token no longer registered: {Token}", token[..Math.Min(20, token.Length)]);
            return false;
        }
        catch (FirebaseMessagingException ex)
        {
            _logger.LogError(ex, "FCM send failed: {ErrorCode}", ex.MessagingErrorCode);
            throw;
        }
    }

    public async Task<bool> SendToTopicAsync(string topic, string title, string body, IDictionary<string, string>? data = null)
    {
        var message = new Message
        {
            Topic = topic,
            Notification = new Notification
            {
                Title = title,
                Body = body
            },
            Data = data?.ToDictionary(kv => kv.Key, kv => kv.Value),
            Android = new AndroidConfig
            {
                Priority = Priority.High,
                Notification = new AndroidNotification
                {
                    Sound = "default",
                    ClickAction = "FLUTTER_NOTIFICATION_CLICK"
                }
            }
        };

        try
        {
            await FirebaseMessaging.DefaultInstance.SendAsync(message);
            return true;
        }
        catch (FirebaseMessagingException ex)
        {
            _logger.LogError(ex, "FCM topic send failed for topic {Topic}: {ErrorCode}", topic, ex.MessagingErrorCode);
            return false;
        }
    }
}
