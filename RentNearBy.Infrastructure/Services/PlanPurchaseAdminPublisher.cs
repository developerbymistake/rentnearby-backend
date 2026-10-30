using System.Text.Json;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;

namespace RentNearBy.Infrastructure.Services;

// Best-effort: a RabbitMQ outage must never fail a purchase sync or an RTDN, so every publish error is swallowed.
public static class PlanPurchaseAdminPublisher
{
    public const string Queue = "plan.purchase.admin";

    public static async Task PublishAsync(IRabbitMqPublisher publisher, PlanPurchaseAdminMessage message, ILogger? logger = null)
    {
        try
        {
            await publisher.PublishAsync(Queue, JsonSerializer.Serialize(message));
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to publish {Queue} for purchase {PurchaseId}", Queue, message.PurchaseId);
        }
    }

    public static string? KindOf(PlayApplyKind kind) => kind switch
    {
        PlayApplyKind.Granted or PlayApplyKind.Resubscribed or PlayApplyKind.Reattached => PlanPurchaseAdminKinds.Granted,
        PlayApplyKind.Renewed => PlanPurchaseAdminKinds.Renewed,
        PlayApplyKind.Switched => PlanPurchaseAdminKinds.Switched,
        PlayApplyKind.Recovered => PlanPurchaseAdminKinds.Recovered,
        _ => null,
    };
}
