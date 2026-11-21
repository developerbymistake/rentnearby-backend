using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Interfaces.Owner;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Services.Owner;

namespace RentNearBy.Tests.Owner.Foundation;

// Foundation constants and seams: notification types and routes (5.3, AU-15), the interceptor's watched changes (8.1), the safe
// no-op defaults (12.3), the owner context statement (2.0) and the stay handler seam lines.
public class OwnerSeamsTests
{
    [Theory]
    [InlineData(NotificationTypes.StayEditApproved, "stay_edit_approved")]
    [InlineData(NotificationTypes.StayEditRejected, "stay_edit_rejected")]
    [InlineData(NotificationTypes.FestivalReminder, "festival_reminder")]
    [InlineData(NotificationTypes.PlanLimitsChanged, "plan_limits_changed")]
    [InlineData(NotificationTypes.OwnerMorningReminder, "owner_morning_reminder")]
    [InlineData(NotificationTypes.PlansStarting, "plans_starting")]
    public void Owner_push_types_have_their_wire_values(string type, string wire)
    {
        Assert.Equal(wire, NotificationTypes.ToWireValue(type));
        Assert.Contains(type, NotificationTypes.OwnerPushKinds);
    }

    [Fact]
    public void Owner_routes_are_named_once()
    {
        Assert.Equal("/stay-calendar", UserNotificationRoutes.StayCalendar);
        Assert.Equal("/stay-special-prices", UserNotificationRoutes.StaySpecialPrices);
        Assert.Equal("/stay-plan-visible", UserNotificationRoutes.StayPlanVisible);
        Assert.Equal("/taxi-plan-visible", UserNotificationRoutes.TaxiPlanVisible);
        Assert.Equal("/my-taxi", UserNotificationRoutes.TaxiRoutesAndFares);
        Assert.Equal("/main", UserNotificationRoutes.OwnerHome);
        Assert.Equal("/trip-settle", UserNotificationRoutes.TripSettle);
        Assert.Equal("/trip-replies", UserNotificationRoutes.TripReplies);
    }

    [Theory]
    [InlineData(NoticeKinds.StayEditApproved, ListingKinds.Stay, "/my-stays")]
    [InlineData(NoticeKinds.StayEditRejected, ListingKinds.Stay, "/my-stays")]
    [InlineData(NoticeKinds.FestivalReminder, ListingKinds.Stay, "/stay-special-prices")]
    [InlineData(NoticeKinds.FestivalReminder, ListingKinds.Taxi, "/my-taxi")]
    [InlineData(NoticeKinds.PlanLimitsChanged, ListingKinds.Stay, "/stay-plan-visible")]
    [InlineData(NoticeKinds.PlanLimitsChanged, ListingKinds.Taxi, "/taxi-plan-visible")]
    [InlineData(NoticeKinds.PlansStarting, ListingKinds.Taxi, "/plans")]
    public void Owner_notice_kinds_route_by_listing_kind(string kind, string listingKind, string route)
    {
        var festival = Guid.NewGuid();
        var r = NoticeRoutes.For(kind, festival, null, listingKind);
        Assert.Equal(route, r.Route);
        if (kind == NoticeKinds.FestivalReminder && listingKind == ListingKinds.Stay) Assert.Equal(festival.ToString(), r.Args["festivalId"]);
        if (kind == NoticeKinds.FestivalReminder && listingKind == ListingKinds.Taxi) Assert.Equal("routes", r.Args["section"]);
    }

    [Theory]
    [InlineData(EntityState.Added, "Stay", false, true)]
    [InlineData(EntityState.Added, "Taxi", false, true)]
    [InlineData(EntityState.Modified, "Stay", true, true)]
    [InlineData(EntityState.Modified, "Stay", false, false)]
    [InlineData(EntityState.Unchanged, "Stay", true, false)]
    [InlineData(EntityState.Deleted, "Stay", true, false)]
    [InlineData(EntityState.Added, "Other", false, false)]
    public void Interceptor_watches_adds_and_watched_column_changes_of_stay_and_taxi_enquiries(EntityState state, string kind, bool watchedChanged, bool expected)
        => Assert.Equal(expected, OwnerCalendarSyncInterceptor.IsWatched(state, kind, p => watchedChanged && p == nameof(TripEnquiry.Status)));

    [Theory]
    [InlineData(EntityState.Added, TripEnquiryStatuses.Asked, false)]
    [InlineData(EntityState.Added, TripEnquiryStatuses.BookedByTraveller, false)]
    [InlineData(EntityState.Added, TripEnquiryStatuses.ConfirmedByBoth, true)]
    [InlineData(EntityState.Modified, TripEnquiryStatuses.Asked, true)]
    [InlineData(EntityState.Modified, TripEnquiryStatuses.CancelledByTraveller, true)]
    public void Interceptor_skips_a_new_enquiry_that_is_not_confirmed(EntityState state, string status, bool expected)
        => Assert.Equal(expected, OwnerCalendarSyncInterceptor.IsWatched(state, TripEnquiryKinds.Stay, p => p == nameof(TripEnquiry.Status), status));

    [Theory]
    [InlineData(TripEnquiryStatuses.Asked, TripEnquiryStatuses.Offered, true, false)]
    [InlineData(TripEnquiryStatuses.Offered, TripEnquiryStatuses.Expired, true, false)]
    [InlineData(TripEnquiryStatuses.Asked, TripEnquiryStatuses.Asked, false, false)]
    [InlineData(TripEnquiryStatuses.ConfirmedByBoth, TripEnquiryStatuses.CancelledByTraveller, true, true)]
    [InlineData(TripEnquiryStatuses.Offered, TripEnquiryStatuses.ConfirmedByBoth, true, true)]
    [InlineData(TripEnquiryStatuses.ConfirmedByBoth, TripEnquiryStatuses.ConfirmedByBoth, false, true)]
    [InlineData(TripEnquiryStatuses.CancelledByTraveller, TripEnquiryStatuses.CancelledByTraveller, true, true)]
    public void Interceptor_skips_a_change_that_never_touches_a_confirmed_booking(string original, string current, bool statusModified, bool expected)
        => Assert.Equal(expected, OwnerCalendarSyncInterceptor.IsWatched(EntityState.Modified, TripEnquiryKinds.Stay,
            p => p == nameof(TripEnquiry.CheckIn) || (statusModified && p == nameof(TripEnquiry.Status)), current, original));

    [Theory]
    [InlineData(nameof(TripEnquiry.Status), true)]
    [InlineData(nameof(TripEnquiry.CheckIn), true)]
    [InlineData(nameof(TripEnquiry.CheckOut), true)]
    [InlineData(nameof(TripEnquiry.AcceptedOfferId), true)]
    [InlineData(nameof(TripEnquiry.AgreedTotal), true)]
    [InlineData(nameof(TripEnquiry.UpdatedAt), false)]
    [InlineData(nameof(TripEnquiry.ContextJson), false)]
    public void Interceptor_watched_columns_are_the_contract_five(string column, bool expected)
        => Assert.Equal(expected, OwnerCalendarSyncInterceptor.IsWatched(EntityState.Modified, TripEnquiryKinds.Stay, p => p == column));

    [Fact]
    public void Interceptor_with_the_null_sync_is_inactive()
    {
        Assert.False(new OwnerCalendarSyncInterceptor(new NullOwnerCalendarSync()).IsActive);
    }

    [Fact]
    public async Task Null_seams_are_safe_no_ops()
    {
        IOwnerNotifier notifier = new NullOwnerNotifier();
        await notifier.StoreAsync([new NoticeUpsert(Guid.NewGuid(), NoticeKinds.PlansStarting, NoticeKinds.NoTrip, null)]);
        await notifier.PushAsync([new OwnerPush(Guid.NewGuid(), NotificationTypes.PlansStarting, "k", "t", "b", "/plans", "{}", "tag")]);
        await notifier.ClearAsync(NoticeKinds.PlansStarting, NoticeKinds.NoTrip, [Guid.NewGuid()]);
        notifier.Wake();
        await new NullOwnerListingPurge().PurgeAsync(ListingKinds.Stay, [Guid.NewGuid()]);
        Assert.Empty(await new NullOwnerCalendarSync().ManualDatesAsync(null!, Guid.NewGuid(), new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 2)));
        var items = new List<StayNearbyDto> { new() { Id = Guid.NewGuid() } };
        Assert.Same(items, new NullStayDatedPricing().Apply(items, new DateOnly(2026, 1, 1), 2, 2, 1, false, new DateOnly(2026, 1, 1)));
        await new NullStayDetailPricing().ApplyAsync(new StayPublicDto(), null, 1, new DateOnly(2026, 1, 1));
        Assert.False(await new NullPlanReconcileSignal().WaitAsync(TimeSpan.FromMilliseconds(1), CancellationToken.None));
    }

    [Theory]
    [InlineData(ListingKinds.Stay, "\"StayListings\" l", "l.\"SpecialPriceSavesDay\"")]
    [InlineData(ListingKinds.Taxi, "\"TaxiListings\" l", "NULL::date")]
    public void Owner_context_is_one_statement_over_the_live_listing_and_the_active_plan(string kind, string from, string saves)
    {
        var sql = OwnerContextReader.Sql(kind, ["SELECT count(*) FROM \"CalendarEntries\" c WHERE c.\"ListingId\" = l.\"Id\""]);
        Assert.Contains("FROM " + from, sql);
        Assert.Contains(saves, sql);
        Assert.Contains("WHERE l.\"UserId\" = @me AND l.\"IsDeleted\" = false", sql);
        Assert.Contains("u.\"Status\" = 'Active' AND u.\"EndsAt\" > now()", sql);
        Assert.Contains("(SELECT count(*) FROM \"CalendarEntries\" c WHERE c.\"ListingId\" = l.\"Id\")::bigint AS \"X1\"", sql);
        Assert.Contains("NULL::bigint AS \"X4\"", sql);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(sql, @"\bFROM ""(Stay|Taxi)Listings"""));
        Assert.Throws<ArgumentOutOfRangeException>(() => OwnerContextReader.Sql(kind, ["1", "2", "3", "4", "5"]));
        Assert.Throws<ArgumentOutOfRangeException>(() => OwnerContextReader.Sql("Room"));
    }

    [Fact]
    public void Owner_context_build_computes_limits_and_reads_extras()
    {
        var row = new OwnerContextReader.Row
        {
            ListingId = Guid.NewGuid(), Status = "Approved", RemindersOn = true, SpecialPriceSavesDay = new DateOnly(2026, 11, 21),
            SpecialPriceSavesCount = 2, X1 = 7,
        };
        var ctx = OwnerContextReader.Build(Guid.NewGuid(), ListingKinds.Stay, row, Support.Owner.OwnerPlanCatalog.Seeded(), PaymentFeatureStates.On(), new DateOnly(2026, 11, 21));
        Assert.Equal(2, ctx.Limits.Photos);
        Assert.True(ctx.Limits.CanUpgrade);
        Assert.Equal(2, ctx.SpecialPriceSavesToday);
        Assert.Equal(7, ctx.Extra(1));
        Assert.Null(ctx.Extra(2));
        Assert.Null(ctx.Extra(9));
        Assert.Equal(new DateOnly(2026, 11, 30), ctx.Window.MonthTo);
    }

    [Fact]
    public void Stay_handlers_carry_the_check_in_seam_and_one_dated_pass_per_list_read()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "RentNearBy.Api", "Handlers", "StayHandlers.cs"));
        var applies = System.Text.RegularExpressions.Regex.Matches(source, @"StayNearbyRooms\.Apply\(").Count;
        var dated = System.Text.RegularExpressions.Regex.Matches(source, @"items = Dated\(").Count;
        Assert.Equal(applies, dated);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(source, @"IStayDetailPricing>\(\) is \{ \} pricing").Count);
        foreach (var name in new[] { "GetNearby", "GetNearest", "GetById", "GetBySlug" })
        {
            var m = typeof(RentNearBy.Api.Handlers.StayHandlers).GetMethod(name)!;
            Assert.Contains(m.GetParameters(), p => p.Name == "checkIn" && p.ParameterType == typeof(DateOnly?) && p.HasDefaultValue);
        }
    }

    [Fact]
    public void Listing_purge_calls_the_owner_seam_first_and_deletes_hidden_rows()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "RentNearBy.Infrastructure", "Services", "ListingPurgeService.cs"));
        Assert.Contains("await ownerPurge.PurgeAsync(ListingKinds.Stay, stayIds);", source);
        Assert.Contains("await ownerPurge.PurgeAsync(ListingKinds.Taxi, taxiIds);", source);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(source, @"\.IgnoreQueryFilters\(\)").Count);
        Assert.True(source.IndexOf("ownerPurge.PurgeAsync(ListingKinds.Stay", StringComparison.Ordinal) < source.IndexOf("context.StayJobs", StringComparison.Ordinal));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RentNearBy.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found");
    }
}
