using System.Text.Json.Serialization;

namespace RentNearBy.Core.DTOs.Requests;

public class PaymentPlanRequest
{
    [JsonPropertyName("planType")]
    public string PlanType { get; set; } = string.Empty;
}
