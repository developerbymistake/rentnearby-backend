using System.Text.Json.Serialization;

namespace RentNearBy.Core.DTOs.TaxiRequests;

// Contract sections 7 and 10: the ack body and the FCM message parts (every data value is a string).

public class TaxiRingAckDto
{
    public string AckToken { get; set; } = string.Empty;
    public DateTime ReceivedAt { get; set; }
    public bool Shown { get; set; }
    public bool Late { get; set; }
    public bool NotificationsOn { get; set; }
    public bool RingChannelOn { get; set; }
    public string? Manufacturer { get; set; }
    public int AndroidSdk { get; set; }
    public int AppBuild { get; set; }
}

public class TaxiPushAndroidDto
{
    public string Priority { get; set; } = "HIGH";
    public int TtlSeconds { get; set; }
    public string? CollapseKey { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ChannelId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Tag { get; set; }
}

public class TaxiPushNotificationDto
{
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
}

// Ring: data only, notification written as null. Ring end and visible pushes carry a notification block; only the ring and ring end carry android.
public class TaxiPushMessageDto
{
    public Dictionary<string, string> Data { get; set; } = new();
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TaxiPushAndroidDto? Android { get; set; }
    public TaxiPushNotificationDto? Notification { get; set; }
}
