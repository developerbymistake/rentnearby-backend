using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.Interfaces;

namespace RentNearBy.Infrastructure.Services;

// Independent of FcmService by design (see IChatFcmService) — this class does NOT
// share code with FcmService.cs, which stays untouched. The few lines of Firebase-init
// guard below are intentionally duplicated rather than shared: whichever of the two
// singletons DI constructs first performs the one-time FirebaseApp.Create(), the other
// no-ops via the same null-check, and neither class depends on the other.
public class ChatFcmService : IChatFcmService
{
    private readonly ILogger<ChatFcmService> _logger;

    public ChatFcmService(IConfiguration configuration, ILogger<ChatFcmService> logger)
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

    public async Task<bool> SendAsync(string token, string title, string body, Guid conversationId)
    {
        var message = new Message
        {
            Token = token,
            Notification = new Notification
            {
                Title = title,
                Body = body
            },
            Data = new Dictionary<string, string>
            {
                { "conversation_id", conversationId.ToString() }
            },
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
            _logger.LogWarning("Chat FCM token no longer registered: {Token}", token[..Math.Min(20, token.Length)]);
            return false;
        }
        catch (FirebaseMessagingException ex)
        {
            _logger.LogError(ex, "Chat FCM send failed: {ErrorCode}", ex.MessagingErrorCode);
            throw;
        }
    }
}
