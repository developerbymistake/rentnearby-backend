using System.Text.Json;
using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.Plans;

public class PlanPurchaseAdminPublisherTests
{
    [Theory]
    [InlineData(PlayApplyKind.Granted, PlanPurchaseAdminKinds.Granted)]
    [InlineData(PlayApplyKind.Resubscribed, PlanPurchaseAdminKinds.Granted)]
    [InlineData(PlayApplyKind.Reattached, PlanPurchaseAdminKinds.Granted)]
    [InlineData(PlayApplyKind.Renewed, PlanPurchaseAdminKinds.Renewed)]
    [InlineData(PlayApplyKind.Switched, PlanPurchaseAdminKinds.Switched)]
    [InlineData(PlayApplyKind.Recovered, PlanPurchaseAdminKinds.Recovered)]
    [InlineData(PlayApplyKind.Unchanged, null)]
    public void KindOf_ApplyKind_AdminKind(PlayApplyKind kind, string? expected)
        => Assert.Equal(expected, PlanPurchaseAdminPublisher.KindOf(kind));

    [Fact]
    public async Task PublishAsync_Message_GoesToAdminQueueAsJson()
    {
        var publisher = new RecordingPublisher();
        var message = new PlanPurchaseAdminMessage
        {
            Kind = PlanPurchaseAdminKinds.ChargebackReview,
            PurchaseId = TestIds.Purchase1,
            PlayOrderId = "GPA.1",
        };

        await PlanPurchaseAdminPublisher.PublishAsync(publisher, message);

        var (queue, json) = Assert.Single(publisher.Published);
        Assert.Equal("plan.purchase.admin", queue);
        var read = JsonSerializer.Deserialize<PlanPurchaseAdminMessage>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(read);
        Assert.Equal(PlanPurchaseAdminKinds.ChargebackReview, read.Kind);
        Assert.Equal(TestIds.Purchase1, read.PurchaseId);
        Assert.Equal("GPA.1", read.PlayOrderId);
    }

    [Fact]
    public async Task PublishAsync_BrokerDown_Swallowed()
    {
        var publisher = new RecordingPublisher(new InvalidOperationException("broker down"));

        var ex = await Record.ExceptionAsync(() =>
            PlanPurchaseAdminPublisher.PublishAsync(publisher, new PlanPurchaseAdminMessage { Kind = PlanPurchaseAdminKinds.Granted }));

        Assert.Null(ex);
        Assert.Empty(publisher.Published);
    }
}
