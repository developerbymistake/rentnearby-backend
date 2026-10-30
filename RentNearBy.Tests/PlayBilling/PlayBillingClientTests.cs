using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.PlayBilling;

public class PlayBillingClientTests
{
    private const string Token = "purchase-token-123456789";

    [Theory]
    [InlineData("abcdefghijkl", "efghijkl")]
    [InlineData("abcdefgh", "abcdefgh")]
    [InlineData("abc", "abc")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void TokenTail_KeepsLastEight(string? token, string expected)
        => Assert.Equal(expected, PlayBillingClient.TokenTail(token));

    private static object PlusItem() => new
    {
        productId = "room_plus",
        expiryTime = "2026-12-01T00:00:00Z",
        autoRenewingPlan = new
        {
            autoRenewEnabled = true,
            recurringPrice = new { currencyCode = "INR", units = "149", nanos = 500000000 },
        },
        offerDetails = new { basePlanId = "cycle-1m", offerId = "trial-7d" },
        latestSuccessfulOrderId = "GPA.1",
        deferredItemReplacement = new { productId = "room_pro" },
    };

    private static readonly object ProItem = new { productId = "room_pro" };

    [Fact]
    public void Parse_TwoLineItems_MapsEveryField()
    {
        var json = Json.Of(new
        {
            subscriptionState = PlayStates.Active,
            acknowledgementState = PlayAckStates.Acknowledged,
            startTime = "2026-11-01T05:30:00+05:30",
            regionCode = "IN",
            linkedPurchaseToken = "old-token",
            lineItems = new[] { PlusItem(), ProItem },
        });

        var s = PlayBillingClient.Parse(Token, json);

        Assert.Equal(Token, s.PurchaseToken);
        Assert.Equal(PlayStates.Active, s.State);
        Assert.Equal(PlayAckStates.Acknowledged, s.AcknowledgementState);
        Assert.Equal(Dates.Utc(2026, 11, 1), s.StartTime);
        Assert.Equal("IN", s.RegionCode);
        Assert.Equal("old-token", s.LinkedPurchaseToken);
        Assert.Equal(2, s.LineItems.Count);

        var item = s.LineItems[0];
        Assert.Equal("room_plus", item.ProductId);
        Assert.Equal(Dates.Utc(2026, 12, 1), item.ExpiryTime);
        Assert.Equal(DateTimeKind.Utc, item.ExpiryTime!.Value.Kind);
        Assert.Equal(149_500_000L, item.RecurringPriceMicros);
        Assert.Equal("INR", item.RecurringPriceCurrency);
        Assert.True(item.AutoRenewEnabled);
        Assert.Equal("cycle-1m", item.BasePlanId);
        Assert.Equal("trial-7d", item.OfferId);
        Assert.Equal("GPA.1", item.LatestOrderId);
        Assert.Equal("room_pro", item.DeferredProductId);
        Assert.False(item.IsPrepaid);

        var pro = s.LineItems[1];
        Assert.Null(pro.ExpiryTime);
        Assert.Null(pro.AutoRenewEnabled);
        Assert.Null(pro.RecurringPriceMicros);

        Assert.Same(item, s.Entitled);
        Assert.Same(pro, s.DeferredTarget);
        Assert.Equal(json, s.RawJson);
    }

    [Fact]
    public void Parse_SingleItemWithoutOrder_UsesLegacyRootOrderId()
    {
        var json = Json.Of(new { latestOrderId = "GPA.9", lineItems = new[] { new { productId = "room_plus" } } });

        Assert.Equal("GPA.9", PlayBillingClient.Parse(Token, json).LineItems.Single().LatestOrderId);
    }

    [Fact]
    public void Parse_SingleItemWithOrder_KeepsItemOrderId()
    {
        var json = Json.Of(new { latestOrderId = "GPA.9", lineItems = new[] { new { productId = "room_plus", latestSuccessfulOrderId = "GPA.1" } } });

        Assert.Equal("GPA.1", PlayBillingClient.Parse(Token, json).LineItems.Single().LatestOrderId);
    }

    [Fact]
    public void Parse_TwoItems_LegacyOrderIdIgnored()
    {
        var json = Json.Of(new { latestOrderId = "GPA.9", lineItems = new[] { new { productId = "room_plus" }, new { productId = "room_pro" } } });

        Assert.All(PlayBillingClient.Parse(Token, json).LineItems, li => Assert.Null(li.LatestOrderId));
    }

    [Fact]
    public void Parse_ReplacementCancellation_CancelReason()
    {
        var json = Json.Of(new { canceledStateContext = new { replacementCancellation = Json.Empty } });

        Assert.Equal(PlayCancelReasons.Replacement, PlayBillingClient.Parse(Token, json).CancelReason);
    }

    [Fact]
    public void Parse_UserCancellation_CancelReason()
    {
        var json = Json.Of(new { canceledStateContext = new { userInitiatedCancellation = new { cancelTime = "2026-11-01T00:00:00Z" } } });

        Assert.Equal(PlayCancelReasons.User, PlayBillingClient.Parse(Token, json).CancelReason);
    }

    [Fact]
    public void Parse_NoCancelContext_NullReason()
        => Assert.Null(PlayBillingClient.Parse(Token, "{}").CancelReason);

    [Fact]
    public void Parse_EmptyObject_DefaultsAndNoItems()
    {
        var s = PlayBillingClient.Parse(Token, "{}");

        Assert.Equal(PlayStates.Unspecified, s.State);
        Assert.Equal(PlayAckStates.Pending, s.AcknowledgementState);
        Assert.False(s.IsTestPurchase);
        Assert.Empty(s.LineItems);
        Assert.Null(s.Entitled);
        Assert.False(s.IsEntitled);
    }

    [Fact]
    public void Parse_TestPurchase_Flagged()
        => Assert.True(PlayBillingClient.Parse(Token, Json.Of(new { testPurchase = Json.Empty })).IsTestPurchase);

    [Fact]
    public void Parse_ExternalIdentifiers_Mapped()
    {
        var json = Json.Of(new
        {
            externalAccountIdentifiers = new { obfuscatedExternalAccountId = "acct", obfuscatedExternalProfileId = "prof" },
        });

        var s = PlayBillingClient.Parse(Token, json);

        Assert.Equal("acct", s.ObfuscatedAccountId);
        Assert.Equal("prof", s.ObfuscatedProfileId);
    }

    [Fact]
    public void Parse_PausedContext_AutoResumeInUtc()
    {
        var json = Json.Of(new { pausedStateContext = new { autoResumeTime = "2026-12-05T05:30:00+05:30" } });

        var resume = PlayBillingClient.Parse(Token, json).PausedAutoResumeTime;

        Assert.Equal(Dates.Utc(2026, 12, 5), resume);
        Assert.Equal(DateTimeKind.Utc, resume!.Value.Kind);
    }

    [Fact]
    public void Parse_OutOfAppContext_ExpiredToken()
    {
        var json = Json.Of(new { outOfAppPurchaseContext = new { expiredPurchaseToken = "expired" } });

        Assert.Equal("expired", PlayBillingClient.Parse(Token, json).ExpiredPurchaseToken);
    }

    [Fact]
    public void Parse_PrepaidPlan_IsPrepaid()
    {
        var json = Json.Of(new { lineItems = new[] { new { productId = "room_plus", prepaidPlan = Json.Empty } } });

        Assert.True(PlayBillingClient.Parse(Token, json).LineItems.Single().IsPrepaid);
    }

    [Fact]
    public void Parse_ItemWithoutProductId_Skipped()
    {
        var json = Json.Of(new { lineItems = new object[] { new { expiryTime = "2026-12-01T00:00:00Z" }, new { productId = "" }, ProItem } });

        Assert.Equal("room_pro", PlayBillingClient.Parse(Token, json).LineItems.Single().ProductId);
    }

    [Theory]
    [InlineData("\"149\"", 0, 149_000_000L)]
    [InlineData("149", 990000000, 149_990_000L)]
    [InlineData("\"0\"", 500000000, 500_000L)]
    public void Parse_RecurringPrice_UnitsAsStringOrNumber(string unitsJson, int nanos, long expectedMicros)
    {
        var json = "{\"lineItems\":[{\"productId\":\"room_plus\",\"autoRenewingPlan\":{\"recurringPrice\":{\"currencyCode\":\"INR\",\"units\":"
            + unitsJson + ",\"nanos\":" + nanos + "}}}]}";

        Assert.Equal(expectedMicros, PlayBillingClient.Parse(Token, json).LineItems.Single().RecurringPriceMicros);
    }
}
