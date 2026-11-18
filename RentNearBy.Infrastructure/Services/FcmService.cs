using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;

namespace RentNearBy.Infrastructure.Services;

public class FcmService : IFcmService
{
    // Created by the user app at start (notification_service.dart); an unknown id falls back to the default channel.
    private const string GeneralChannelId = "general_notifications";

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
                    ClickAction = "FLUTTER_NOTIFICATION_CLICK",
                    ChannelId = GeneralChannelId
                }
            }
        };

        try
        {
            await FirebaseMessaging.DefaultInstance.SendAsync(message);
            return true;
        }
        catch (FirebaseMessagingException ex)
            when (IsPermanentTokenError(ex.MessagingErrorCode, ex.Message))
        {
            _logger.LogWarning("FCM token rejected ({ErrorCode}): {Token}", ex.MessagingErrorCode, token[..Math.Min(20, token.Length)]);
            return false;
        }
        catch (FirebaseMessagingException ex)
        {
            _logger.LogError(ex, "FCM send failed: {ErrorCode}", ex.MessagingErrorCode);
            throw;
        }
    }

    public Task<bool> SendAsync(string token, string title, string body, string notificationType, NotificationDestination destination)
        => SendAsync(token, title, body, notificationType, destination.ToFcmData());

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
                    ClickAction = "FLUTTER_NOTIFICATION_CLICK",
                    ChannelId = GeneralChannelId
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

    public const int BatchSize = 500;

    public async Task<IReadOnlyList<FcmSendOutcome>> SendEachAsync(IReadOnlyList<FcmMessageSpec> messages, CancellationToken ct = default)
    {
        var outcomes = new FcmSendOutcome[messages.Count];
        for (var from = 0; from < messages.Count; from += BatchSize)
        {
            var take = Math.Min(BatchSize, messages.Count - from);
            var chunk = new List<Message>(take);
            for (var i = 0; i < take; i++) chunk.Add(ToMessage(messages[from + i]));
            try
            {
                var response = await FirebaseMessaging.DefaultInstance.SendEachAsync(chunk, ct);
                for (var i = 0; i < take; i++)
                {
                    var r = response.Responses[i];
                    outcomes[from + i] = Classify(r);
                    if (outcomes[from + i] == FcmSendOutcome.Failed && r.Exception?.MessagingErrorCode == MessagingErrorCode.InvalidArgument)
                        _logger.LogWarning("FCM rejected a message as invalid (token kept): {Reason}", r.Exception.Message);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "FCM batch of {Count} failed", take);
                for (var i = 0; i < take; i++) outcomes[from + i] = FcmSendOutcome.Failed;
            }
        }
        return outcomes;
    }

    public static FcmSendOutcome Classify(SendResponse response)
        => Classify(response.IsSuccess, response.Exception?.MessagingErrorCode, response.Exception?.Message);

    public static FcmSendOutcome Classify(bool isSuccess, MessagingErrorCode? errorCode, string? errorMessage = null)
        => isSuccess ? FcmSendOutcome.Sent
            : IsPermanentTokenError(errorCode, errorMessage) ? FcmSendOutcome.InvalidToken
            : FcmSendOutcome.Failed;

    // Unregistered and SenderIdMismatch always mean the token is dead. InvalidArgument also covers a bad message (data too big,
    // bad TTL, reserved key), so it marks the token dead only when FCM names the registration token as the bad argument.
    public static bool IsPermanentTokenError(MessagingErrorCode? errorCode, string? errorMessage = null)
        => errorCode is MessagingErrorCode.Unregistered or MessagingErrorCode.SenderIdMismatch
            || (errorCode is MessagingErrorCode.InvalidArgument
                && errorMessage != null
                && errorMessage.Contains("registration token", StringComparison.OrdinalIgnoreCase));

    public static Message ToMessage(FcmMessageSpec spec) => new()
    {
        Token = spec.Token,
        Notification = new Notification { Title = spec.Title, Body = spec.Body },
        Data = spec.Data.ToDictionary(kv => kv.Key, kv => kv.Value),
        Android = new AndroidConfig
        {
            Priority = Priority.High,
            Notification = new AndroidNotification
            {
                Sound = "default",
                ClickAction = "FLUTTER_NOTIFICATION_CLICK",
                ChannelId = GeneralChannelId,
                Tag = spec.Tag,
            }
        }
    };
}
