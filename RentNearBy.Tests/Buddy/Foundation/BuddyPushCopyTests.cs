using RentNearBy.Core.Models.Buddy;

namespace RentNearBy.Tests.Buddy.Foundation;

public class BuddyPushCopyTests
{
    private static readonly Guid Trip = new("11111111-2222-3333-4444-555555555555");
    private static readonly Guid User = new("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly Guid Other = new("99999999-8888-7777-6666-555555555555");
    private static readonly DateTime Now = new(2026, 5, 20, 10, 7, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(0, "₹0")]
    [InlineData(999, "₹999")]
    [InlineData(1000, "₹1,000")]
    [InlineData(11816, "₹11,816")]
    [InlineData(32300, "₹32,300")]
    [InlineData(500000, "₹5,00,000")]
    [InlineData(5000000, "₹50,00,000")]
    [InlineData(12345678, "₹1,23,45,678")]
    public void Rupees_use_indian_grouping(long amount, string expected) => Assert.Equal(expected, BuddyPushCopy.Rupees(amount));

    private static IEnumerable<PushSpec> All() =>
    [
        BuddyPushCopy.BuddyRequestReceived(User, Other, "Rahul", "Bakhli#48291736"),
        BuddyPushCopy.BuddyRequestAccepted(User, Other, "Aman"),
        BuddyPushCopy.TripInvite(User, Other, 3, Trip, "Rahul", "Uttarakhand Hills Trip"),
        BuddyPushCopy.TripMemberJoined(User, Other, 2, Trip, "Aman", "Uttarakhand Hills Trip", 4),
        BuddyPushCopy.TripPlanUpdated(User, Trip, "Rahul", "Uttarakhand Hills Trip", Now),
        BuddyPushCopy.TripMemberRemoved(User, Other, 5, Trip, "Rahul", "Uttarakhand Hills Trip"),
        BuddyPushCopy.TripDeletedForViewer(User, Trip, "Rahul", "Uttarakhand Hills Trip"),
        BuddyPushCopy.TripExpensesUpdated(User, Trip, "Rahul", "Uttarakhand Hills Trip", Now),
        BuddyPushCopy.ManualReminder(User, Trip, "Uttarakhand Hills Trip", [("Rahul", 11816)], new DateOnly(2026, 5, 20)),
        BuddyPushCopy.AutoReminder(User, Trip, "Uttarakhand Hills Trip", [("Rahul", 100), ("Aman", 200)], 7),
        BuddyPushCopy.PaymentSaidPaid(User, Other, Trip, "Aman", 5000, "Uttarakhand Hills Trip", "Rahul", toOwner: true),
        BuddyPushCopy.PaymentReceived(User, Other, Trip, "Aman", "Rahul", 5000, toPayer: true),
        BuddyPushCopy.PaymentWithdrawn(User, Other, Trip, "Aman", 5000, "Uttarakhand Hills Trip"),
        BuddyPushCopy.PaymentNotReceived(User, Other, Trip, "Rahul", 5000),
        // Bakhli social pushes ride the same kind list (traveller-only delivery).
        RentNearBy.Core.Models.Social.SocialPushCopy.FollowRequest(User, Other, "Aman"),
        RentNearBy.Core.Models.Social.SocialPushCopy.NewFollower(User, Other, "Aman", Now),
        RentNearBy.Core.Models.Social.SocialPushCopy.FollowAccepted(User, Other, "Aman"),
        RentNearBy.Core.Models.Social.SocialPushCopy.JoinRequest(User, Other, Trip, "Aman"),
        RentNearBy.Core.Models.Social.SocialPushCopy.JoinAccepted(User, Other, Trip, "Rahul"),
        RentNearBy.Core.Models.Social.SocialPushCopy.DirectMessage(User, Other, "Aman", Now),
        RentNearBy.Core.Models.Social.SocialPushCopy.TripMessage(User, Other, Now),
    ];

    [Fact]
    public void Every_push_kind_is_built_with_a_wire_value_and_a_short_key()
    {
        var specs = All().ToList();
        Assert.Equal(NotificationTypes.BuddyPushKinds.OrderBy(k => k), specs.Select(s => s.PushKind).Distinct().OrderBy(k => k));
        foreach (var s in specs)
        {
            Assert.True(s.Key.Length <= BuddyPushCopy.MaxKeyLength, s.Key);
            Assert.NotEqual(s.PushKind, NotificationTypes.ToWireValue(s.PushKind));
            Assert.DoesNotContain("upi", s.Body, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith("{", s.ArgsJson);
            Assert.StartsWith("/", s.Route);
        }
    }

    [Fact]
    public void Keys_and_tags_follow_the_contract_table()
    {
        var n = Trip.ToString("N");
        var u = User.ToString("N");
        var o = Other.ToString("N");
        Assert.Equal($"buddyreq:{o}", BuddyPushCopy.BuddyRequestReceived(User, Other, "Rahul", "Bakhli#48291736").Key);
        Assert.Equal($"tripinv:{o}:3", BuddyPushCopy.TripInvite(User, Other, 3, Trip, "R", "T").Key);
        Assert.Equal($"tripinv:{n}", BuddyPushCopy.TripInvite(User, Other, 3, Trip, "R", "T").Tag);
        Assert.Equal($"notrecv:{o}", BuddyPushCopy.PaymentNotReceived(User, Other, Trip, "R", 1).Key);
        Assert.Equal($"remind:{n}:{u}:20260520", BuddyPushCopy.ManualReminder(User, Trip, "T", [("R", 1)], new DateOnly(2026, 5, 20)).Key);
        Assert.Equal($"autoremind:{n}:{u}:3", BuddyPushCopy.AutoReminder(User, Trip, "T", [("R", 1)], 3).Key);
        Assert.Equal($"settle:{n}", BuddyPushCopy.AutoReminder(User, Trip, "T", [("R", 1)], 3).Tag);
    }

    [Fact]
    public void Expense_push_coalesces_per_ten_minutes_and_waits_sixty_seconds()
    {
        var a = BuddyPushCopy.TripExpensesUpdated(User, Trip, "R", "T", new DateTime(2026, 5, 20, 10, 0, 0, DateTimeKind.Utc));
        var b = BuddyPushCopy.TripExpensesUpdated(User, Trip, "R", "T", new DateTime(2026, 5, 20, 10, 9, 59, DateTimeKind.Utc));
        var c = BuddyPushCopy.TripExpensesUpdated(User, Trip, "R", "T", new DateTime(2026, 5, 20, 10, 10, 0, DateTimeKind.Utc));
        Assert.Equal(a.Key, b.Key);
        Assert.NotEqual(a.Key, c.Key);
        Assert.Equal(TimeSpan.FromSeconds(60), a.Delay);
        Assert.Equal(TimeSpan.FromMinutes(10), BuddyPushCopy.TripPlanUpdated(User, Trip, "R", "T", Now).Delay);
    }

    [Fact]
    public void Texts_match_the_push_table()
    {
        Assert.Equal("You owe Rahul ₹11,816.", BuddyPushCopy.ManualReminder(User, Trip, "T", [("Rahul", 11816)], new DateOnly(2026, 5, 20)).Body);
        Assert.Equal("You owe ₹300 on Goa.", BuddyPushCopy.AutoReminder(User, Trip, "Goa", [("Rahul", 100), ("Aman", 200)], 7).Body);
        Assert.Equal("Goa was deleted.", BuddyPushCopy.TripDeletedForViewer(User, Trip, null, "Goa").Body);
        Assert.Equal("Rahul deleted Goa.", BuddyPushCopy.TripDeletedForViewer(User, Trip, "Rahul", "Goa").Body);
        Assert.Equal("Aman joined your trip", BuddyPushCopy.TripMemberJoined(User, Other, 1, Trip, "Aman", "Goa", 3).Title);
        Assert.Equal("₹5,000 for Goa. Rahul will mark it as received.",
            BuddyPushCopy.PaymentSaidPaid(User, Other, Trip, "Aman", 5000, "Goa", "Rahul", toOwner: false).Body);
        Assert.Equal("Aman's ₹5,000 to you is marked as received.",
            BuddyPushCopy.PaymentReceived(User, Other, Trip, "Aman", "Priya", 5000, toPayer: false).Body);
    }
}
