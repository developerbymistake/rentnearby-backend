using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Entities.TaxiRequests;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Tests.TaxiRequests;

// Model only, no connection.
public class TaxiRequestModelTests
{
    private static readonly IModel DesignModel = BuildModel();

    private static IModel BuildModel()
    {
        using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=localhost;Database=design_only", o => o.UseNetTopologySuite())
            .Options);
        return db.GetService<IDesignTimeModel>().Model;
    }

    private static IEntityType Entity<T>() => DesignModel.FindEntityType(typeof(T))!;

    private static IIndex Index<T>(string name)
    {
        var matches = Entity<T>().GetIndexes().Where(i => i.GetDatabaseName() == name).ToList();
        return Assert.Single(matches);
    }

    private static string[] Columns(IIndex index) => index.Properties.Select(p => p.Name).ToArray();

    private static string Check<T>(string name)
    {
        var matches = Entity<T>().GetCheckConstraints().Where(c => c.Name == name).ToList();
        return Assert.Single(matches).Sql;
    }

    private static IProperty Prop<T>(string name) => Entity<T>().FindProperty(name)
        ?? throw new Xunit.Sdk.XunitException($"{typeof(T).Name}.{name} is missing");

    private static void Column<T>(string name, string storeType, bool nullable)
    {
        var p = Prop<T>(name);
        Assert.Equal(storeType, p.GetColumnType());
        Assert.Equal(nullable, p.IsColumnNullable());
    }

    private static void AssertPartialUnique(IIndex ix, string[] columns, string? filter)
    {
        Assert.Equal(columns, Columns(ix));
        Assert.True(ix.IsUnique);
        Assert.Equal(filter, ix.GetFilter());
    }

    private static void AssertPlain(IIndex ix, string[] columns, string? filter, bool[]? descending = null)
    {
        Assert.Equal(columns, Columns(ix));
        Assert.False(ix.IsUnique);
        Assert.Equal(filter, ix.GetFilter());
        if (descending is not null) Assert.Equal(descending, ix.IsDescending);
    }

    private static void AssertNoForeignKeys<T>()
        => Assert.Empty(Entity<T>().GetForeignKeys());

    [Fact]
    public void Taxi_requests_table_has_its_columns_checks_and_indexes()
    {
        var e = Entity<TaxiRequest>();
        Assert.Equal("TaxiRequests", e.GetTableName());
        Assert.Equal("gen_random_uuid()", Prop<TaxiRequest>(nameof(TaxiRequest.Id)).GetDefaultValueSql());
        Column<TaxiRequest>(nameof(TaxiRequest.Status), "character varying(20)", false);
        Column<TaxiRequest>(nameof(TaxiRequest.EndReason), "character varying(32)", true);
        Column<TaxiRequest>(nameof(TaxiRequest.CancelChip), "character varying(20)", true);
        Column<TaxiRequest>(nameof(TaxiRequest.AdminNote), "character varying(300)", true);
        Column<TaxiRequest>(nameof(TaxiRequest.WantPickup), "boolean", false);
        Column<TaxiRequest>(nameof(TaxiRequest.WantSightseeing), "boolean", false);
        Column<TaxiRequest>(nameof(TaxiRequest.WantDrop), "boolean", false);
        Column<TaxiRequest>(nameof(TaxiRequest.PickupCityKey), "bigint", true);
        Column<TaxiRequest>(nameof(TaxiRequest.PickupCityName), "character varying(120)", true);
        Column<TaxiRequest>(nameof(TaxiRequest.DropCityKey), "bigint", true);
        Column<TaxiRequest>(nameof(TaxiRequest.DropCityName), "character varying(120)", true);
        Column<TaxiRequest>(nameof(TaxiRequest.DropSameAsPickup), "boolean", false);
        Column<TaxiRequest>(nameof(TaxiRequest.DispatchCityKey), "bigint", false);
        Column<TaxiRequest>(nameof(TaxiRequest.MatchToCityKey), "bigint", false);
        Column<TaxiRequest>(nameof(TaxiRequest.MatchFromCityKey), "bigint", true);
        Column<TaxiRequest>(nameof(TaxiRequest.Seats), "smallint", false);
        Column<TaxiRequest>(nameof(TaxiRequest.Travellers), "smallint", false);
        Column<TaxiRequest>(nameof(TaxiRequest.DateFrom), "date", false);
        Column<TaxiRequest>(nameof(TaxiRequest.DateTo), "date", false);
        Column<TaxiRequest>(nameof(TaxiRequest.SightseeingDaysJson), "jsonb", false);
        Column<TaxiRequest>(nameof(TaxiRequest.TripSnapshotJson), "jsonb", false);
        Column<TaxiRequest>(nameof(TaxiRequest.AcceptTarget), "smallint", false);
        Column<TaxiRequest>(nameof(TaxiRequest.AcceptedCount), "smallint", false);
        Column<TaxiRequest>(nameof(TaxiRequest.SelectedOfferId), "uuid", true);
        Column<TaxiRequest>(nameof(TaxiRequest.Version), "bigint", false);
        Column<TaxiRequest>(nameof(TaxiRequest.QueueWaitStartedAt), "timestamp with time zone", true);
        Column<TaxiRequest>(nameof(TaxiRequest.WaitRound), "integer", false);
        Column<TaxiRequest>(nameof(TaxiRequest.QueueExhaustedAt), "timestamp with time zone", true);
        Column<TaxiRequest>(nameof(TaxiRequest.ClientRequestId), "uuid", true);
        Column<TaxiRequest>(nameof(TaxiRequest.CreatedAt), "timestamp with time zone", false);
        Column<TaxiRequest>(nameof(TaxiRequest.UpdatedAt), "timestamp with time zone", false);
        Column<TaxiRequest>(nameof(TaxiRequest.ClosedAt), "timestamp with time zone", true);
        Assert.Equal((short)3, Prop<TaxiRequest>(nameof(TaxiRequest.AcceptTarget)).GetDefaultValue());
        Assert.Equal((short)0, Prop<TaxiRequest>(nameof(TaxiRequest.AcceptedCount)).GetDefaultValue());
        Assert.Equal(1L, Prop<TaxiRequest>(nameof(TaxiRequest.Version)).GetDefaultValue());
        Assert.Equal(0, Prop<TaxiRequest>(nameof(TaxiRequest.WaitRound)).GetDefaultValue());
        Assert.Null(e.FindProperty("xmin"));

        Assert.Equal("\"Status\" IN ('Open', 'Collecting', 'Selected', 'Booked', 'InProgress', 'Completed', 'Cancelled', 'Expired')",
            Check<TaxiRequest>("ck_taxirequests_status"));
        Assert.Equal("\"WantPickup\" OR \"WantSightseeing\" OR \"WantDrop\"", Check<TaxiRequest>("ck_taxirequests_sections"));
        Assert.Equal("\"Seats\" IN (4, 6, 7, 12)", Check<TaxiRequest>("ck_taxirequests_seats"));
        Assert.Equal("\"DateTo\" >= \"DateFrom\"", Check<TaxiRequest>("ck_taxirequests_dates"));
        Assert.Equal("\"AcceptedCount\" >= 0 AND \"AcceptedCount\" <= \"AcceptTarget\" AND \"AcceptTarget\" <= 9",
            Check<TaxiRequest>("ck_taxirequests_accepts"));

        AssertPartialUnique(Index<TaxiRequest>("ux_taxirequests_client"),
            ["TravellerUserId", "ClientRequestId"], "\"ClientRequestId\" IS NOT NULL");
        AssertPlain(Index<TaxiRequest>("ix_taxirequests_dispatch"),
            ["DispatchCityKey", "CreatedAt", "Id"], "\"Status\" IN ('Open', 'Collecting')");
        AssertPlain(Index<TaxiRequest>("ix_taxirequests_traveller"),
            ["TravellerUserId", "CreatedAt"], null, [false, true]);
        AssertPlain(Index<TaxiRequest>("ix_taxirequests_closed"), ["ClosedAt"], "\"ClosedAt\" IS NOT NULL");

        var fks = e.GetForeignKeys().ToList();
        Assert.Equal(2, fks.Count);
        Assert.Contains(fks, f => f.PrincipalEntityType.ClrType == typeof(Trip)
            && f.Properties.Single().Name == nameof(TaxiRequest.TripId) && f.DeleteBehavior == DeleteBehavior.Cascade);
        Assert.Contains(fks, f => f.PrincipalEntityType.ClrType == typeof(User)
            && f.Properties.Single().Name == nameof(TaxiRequest.TravellerUserId) && f.DeleteBehavior == DeleteBehavior.Cascade);
    }

    [Fact]
    public void One_active_request_per_trip_index_is_partial_unique()
        => AssertPartialUnique(Index<TaxiRequest>("ux_taxirequests_active_trip"),
            ["TripId"], "\"Status\" IN ('Open', 'Collecting', 'Selected', 'Booked', 'InProgress')");

    [Fact]
    public void Taxi_request_offers_table_has_its_columns_checks_and_indexes()
    {
        var e = Entity<TaxiRequestOffer>();
        Assert.Equal("TaxiRequestOffers", e.GetTableName());
        Assert.Equal("gen_random_uuid()", Prop<TaxiRequestOffer>(nameof(TaxiRequestOffer.Id)).GetDefaultValueSql());
        Column<TaxiRequestOffer>(nameof(TaxiRequestOffer.RequestId), "uuid", false);
        Column<TaxiRequestOffer>(nameof(TaxiRequestOffer.DriverListingId), "uuid", false);
        Column<TaxiRequestOffer>(nameof(TaxiRequestOffer.DriverUserId), "uuid", false);
        Column<TaxiRequestOffer>(nameof(TaxiRequestOffer.Status), "character varying(20)", false);
        Column<TaxiRequestOffer>(nameof(TaxiRequestOffer.EndReason), "character varying(32)", true);
        Column<TaxiRequestOffer>(nameof(TaxiRequestOffer.Chip), "character varying(20)", true);
        Column<TaxiRequestOffer>(nameof(TaxiRequestOffer.SeqNo), "integer", false);
        Column<TaxiRequestOffer>(nameof(TaxiRequestOffer.OfferedAt), "timestamp with time zone", false);
        Column<TaxiRequestOffer>(nameof(TaxiRequestOffer.ExpiresAt), "timestamp with time zone", false);
        Column<TaxiRequestOffer>(nameof(TaxiRequestOffer.RespondedAt), "timestamp with time zone", true);
        Column<TaxiRequestOffer>(nameof(TaxiRequestOffer.PushReceivedAt), "timestamp with time zone", true);
        Column<TaxiRequestOffer>(nameof(TaxiRequestOffer.PushLate), "boolean", false);
        Column<TaxiRequestOffer>(nameof(TaxiRequestOffer.SelectedAt), "timestamp with time zone", true);
        Column<TaxiRequestOffer>(nameof(TaxiRequestOffer.ConfirmBy), "timestamp with time zone", true);
        Column<TaxiRequestOffer>(nameof(TaxiRequestOffer.ClosedAt), "timestamp with time zone", true);
        Assert.Equal(false, Prop<TaxiRequestOffer>(nameof(TaxiRequestOffer.PushLate)).GetDefaultValue());
        Assert.Null(e.FindProperty("xmin"));

        Assert.Equal("\"Status\" IN ('Ringing', 'Accepted', 'Declined', 'Missed', 'Selected', 'NotSelected', 'NotAgreed', 'Confirmed', 'Withdrawn', 'Closed')",
            Check<TaxiRequestOffer>("ck_taxioffers_status"));

        var pair = Index<TaxiRequestOffer>("ux_taxioffers_request_driver");
        Assert.Equal(["RequestId", "DriverListingId"], Columns(pair));
        Assert.True(pair.IsUnique);
        Assert.Null(pair.GetFilter());
        AssertPlain(Index<TaxiRequestOffer>("ix_taxioffers_driver_list"), ["DriverListingId", "OfferedAt"], null, [false, true]);
        AssertPlain(Index<TaxiRequestOffer>("ix_taxioffers_request"), ["RequestId", "Status"], null);
        AssertPlain(Index<TaxiRequestOffer>("ix_taxioffers_driver_selected"), ["DriverListingId"], "\"Status\" = 'Selected'");

        var fk = Assert.Single(e.GetForeignKeys());
        Assert.Equal(typeof(TaxiRequest), fk.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Cascade, fk.DeleteBehavior);
    }

    [Fact]
    public void Offers_have_no_foreign_key_to_listings()
    {
        foreach (var type in new[] { typeof(TaxiRequestOffer), typeof(TaxiBooking), typeof(TaxiRating), typeof(DriverDispatchState) })
        {
            var fks = DesignModel.FindEntityType(type)!.GetForeignKeys();
            Assert.DoesNotContain(fks, f => f.PrincipalEntityType.ClrType == typeof(TaxiListing));
            Assert.DoesNotContain(fks, f => f.PrincipalEntityType.ClrType == typeof(User));
        }
    }

    [Fact]
    public void One_ringing_offer_per_driver_index_is_partial_unique()
        => AssertPartialUnique(Index<TaxiRequestOffer>("ux_taxioffers_driver_ringing"),
            ["DriverListingId"], "\"Status\" = 'Ringing'");

    [Fact]
    public void Taxi_bookings_table_has_its_columns_checks_and_indexes()
    {
        var e = Entity<TaxiBooking>();
        Assert.Equal("TaxiBookings", e.GetTableName());
        foreach (var id in new[] { "RequestId", "OfferId", "TripId", "DriverListingId", "DriverUserId", "TravellerUserId" })
            Column<TaxiBooking>(id, "uuid", false);
        Column<TaxiBooking>(nameof(TaxiBooking.DateFrom), "date", false);
        Column<TaxiBooking>(nameof(TaxiBooking.DateTo), "date", false);
        Column<TaxiBooking>(nameof(TaxiBooking.Status), "character varying(20)", false);
        Column<TaxiBooking>(nameof(TaxiBooking.CancelledBy), "character varying(12)", true);
        Column<TaxiBooking>(nameof(TaxiBooking.CancelChip), "character varying(20)", true);
        Column<TaxiBooking>(nameof(TaxiBooking.CompletedBy), "character varying(12)", true);
        Column<TaxiBooking>(nameof(TaxiBooking.TripName), "character varying(80)", false);
        Column<TaxiBooking>(nameof(TaxiBooking.PickupCityName), "character varying(120)", false);
        Column<TaxiBooking>(nameof(TaxiBooking.DriverName), "character varying(120)", false);
        Column<TaxiBooking>(nameof(TaxiBooking.VehicleName), "character varying(120)", false);
        Column<TaxiBooking>(nameof(TaxiBooking.Seats), "smallint", false);
        Column<TaxiBooking>(nameof(TaxiBooking.TravellerName), "character varying(120)", false);
        Column<TaxiBooking>(nameof(TaxiBooking.BookedAt), "timestamp with time zone", false);
        Column<TaxiBooking>(nameof(TaxiBooking.StartedAt), "timestamp with time zone", true);
        Column<TaxiBooking>(nameof(TaxiBooking.CompletedAt), "timestamp with time zone", true);
        Column<TaxiBooking>(nameof(TaxiBooking.CancelledAt), "timestamp with time zone", true);

        Assert.Equal("\"Status\" IN ('Booked', 'InProgress', 'Completed', 'Cancelled')", Check<TaxiBooking>("ck_taxibookings_status"));
        Assert.Equal("\"DateTo\" >= \"DateFrom\"", Check<TaxiBooking>("ck_taxibookings_dates"));

        AssertPartialUnique(Index<TaxiBooking>("ux_taxibookings_request_active"),
            ["RequestId"], "\"Status\" IN ('Booked', 'InProgress')");
        AssertPlain(Index<TaxiBooking>("ix_taxibookings_driver_active"),
            ["DriverListingId", "DateFrom", "DateTo"], "\"Status\" IN ('Booked', 'InProgress')");
        AssertPlain(Index<TaxiBooking>("ix_taxibookings_driver_dates"), ["DriverListingId", "DateFrom"], null, [false, true]);
        AssertPlain(Index<TaxiBooking>("ix_taxibookings_traveller"), ["TravellerUserId", "BookedAt"], null, [false, true]);

        AssertNoForeignKeys<TaxiBooking>();
    }

    [Fact]
    public void Taxi_ratings_table_has_its_columns_checks_and_indexes()
    {
        var e = Entity<TaxiRating>();
        Assert.Equal("TaxiRatings", e.GetTableName());
        Assert.Equal(["BookingId"], e.FindPrimaryKey()!.Properties.Select(p => p.Name).ToArray());
        Column<TaxiRating>(nameof(TaxiRating.DriverListingId), "uuid", false);
        Column<TaxiRating>(nameof(TaxiRating.Stars), "smallint", false);
        Column<TaxiRating>(nameof(TaxiRating.CreatedAt), "timestamp with time zone", false);
        Assert.Equal("\"Stars\" BETWEEN 1 AND 5", Check<TaxiRating>("ck_taxiratings_stars"));
        AssertPlain(Index<TaxiRating>("ix_taxiratings_driver"), ["DriverListingId", "CreatedAt"], null, [false, true]);

        var fk = Assert.Single(e.GetForeignKeys());
        Assert.Equal(typeof(TaxiBooking), fk.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Cascade, fk.DeleteBehavior);
    }

    [Fact]
    public void Driver_dispatch_states_table_has_its_columns()
    {
        var e = Entity<DriverDispatchState>();
        Assert.Equal("DriverDispatchStates", e.GetTableName());
        Assert.Equal(["DriverListingId", "CityKey"], e.FindPrimaryKey()!.Properties.Select(p => p.Name).ToArray());
        Column<DriverDispatchState>(nameof(DriverDispatchState.Pass), "bigint", false);
        Column<DriverDispatchState>(nameof(DriverDispatchState.SeenSeq), "bigint", false);
        Column<DriverDispatchState>(nameof(DriverDispatchState.LastOfferedAt), "timestamp with time zone", true);
        Assert.Equal(0L, Prop<DriverDispatchState>(nameof(DriverDispatchState.Pass)).GetDefaultValue());
        Assert.Equal(0L, Prop<DriverDispatchState>(nameof(DriverDispatchState.SeenSeq)).GetDefaultValue());
        AssertNoForeignKeys<DriverDispatchState>();
    }

    [Fact]
    public void Dispatch_state_has_no_missed_streak_column()
    {
        var names = Entity<DriverDispatchState>().GetProperties().Select(p => p.Name).ToArray();
        Assert.Equal(["CityKey", "DriverListingId", "LastOfferedAt", "Pass", "SeenSeq"], names.Order(StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(names, n => n.Contains("Miss", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Taxi_dispatch_cities_table_has_its_columns()
    {
        var e = Entity<TaxiDispatchCity>();
        Assert.Equal("TaxiDispatchCities", e.GetTableName());
        Assert.Equal(["CityKey"], e.FindPrimaryKey()!.Properties.Select(p => p.Name).ToArray());
        Assert.Equal(ValueGenerated.Never, Prop<TaxiDispatchCity>(nameof(TaxiDispatchCity.CityKey)).ValueGenerated);
        Column<TaxiDispatchCity>(nameof(TaxiDispatchCity.Seq), "bigint", false);
        Column<TaxiDispatchCity>(nameof(TaxiDispatchCity.LastMinPass), "bigint", false);
        Column<TaxiDispatchCity>(nameof(TaxiDispatchCity.UpdatedAt), "timestamp with time zone", false);
        Assert.Equal(0L, Prop<TaxiDispatchCity>(nameof(TaxiDispatchCity.Seq)).GetDefaultValue());
        Assert.Equal(0L, Prop<TaxiDispatchCity>(nameof(TaxiDispatchCity.LastMinPass)).GetDefaultValue());
    }

    [Fact]
    public void Due_actions_table_has_its_columns_checks_and_indexes()
    {
        var e = Entity<DueAction>();
        Assert.Equal("DueActions", e.GetTableName());
        Column<DueAction>(nameof(DueAction.Kind), "character varying(24)", false);
        Column<DueAction>(nameof(DueAction.SubjectId), "uuid", false);
        Column<DueAction>(nameof(DueAction.RoundNo), "integer", false);
        Column<DueAction>(nameof(DueAction.RequestId), "uuid", true);
        Column<DueAction>(nameof(DueAction.DueAt), "timestamp with time zone", false);
        Column<DueAction>(nameof(DueAction.State), "character varying(10)", false);
        Column<DueAction>(nameof(DueAction.LeaseUntil), "timestamp with time zone", true);
        Column<DueAction>(nameof(DueAction.Attempts), "smallint", false);
        Column<DueAction>(nameof(DueAction.LastError), "character varying(500)", true);
        Column<DueAction>(nameof(DueAction.CreatedAt), "timestamp with time zone", false);
        Column<DueAction>(nameof(DueAction.DoneAt), "timestamp with time zone", true);
        Assert.Equal(0, Prop<DueAction>(nameof(DueAction.RoundNo)).GetDefaultValue());
        Assert.Equal((short)0, Prop<DueAction>(nameof(DueAction.Attempts)).GetDefaultValue());

        Assert.Equal("\"Kind\" IN ('OfferExpire', 'ConfirmExpire', 'RequestWaitExpire', 'RequestStaleExpire', 'BookingStart', 'BookingAutoComplete', 'ChooseReminderFirst', 'ChooseReminderSecond')",
            Check<DueAction>("ck_dueactions_kind"));
        Assert.Equal("\"State\" IN ('Pending', 'Running', 'Done', 'Cancelled', 'Skipped', 'Failed')",
            Check<DueAction>("ck_dueactions_state"));

        AssertPartialUnique(Index<DueAction>("ux_dueactions_subject"), ["SubjectId", "Kind", "RoundNo"], null);
        AssertPlain(Index<DueAction>("ix_dueactions_due"), ["DueAt", "Id"], "\"State\" = 'Pending'");
        AssertPlain(Index<DueAction>("ix_dueactions_lease"), ["LeaseUntil"], "\"State\" = 'Running'");
        AssertPlain(Index<DueAction>("ix_dueactions_request"), ["RequestId"], "\"State\" = 'Pending'");
        AssertPlain(Index<DueAction>("ix_dueactions_done"), ["DoneAt"], "\"DoneAt\" IS NOT NULL");
        AssertNoForeignKeys<DueAction>();
    }

    [Fact]
    public void Due_action_done_at_check_exists()
        => Assert.Equal("(\"State\" IN ('Pending','Running')) = (\"DoneAt\" IS NULL)", Check<DueAction>("ck_dueactions_done"));

    [Fact]
    public void Trip_invite_code_is_unique_with_a_database_default()
    {
        Column<Trip>(nameof(Trip.InviteCode), "uuid", false);
        Assert.Equal("gen_random_uuid()", Prop<Trip>(nameof(Trip.InviteCode)).GetDefaultValueSql());
        var ix = Index<Trip>("ux_trips_invite_code");
        Assert.Equal(["InviteCode"], Columns(ix));
        Assert.True(ix.IsUnique);
        Assert.Null(ix.GetFilter());
    }

    [Fact]
    public void Trip_name_chosen_defaults_to_false()
    {
        Column<Trip>(nameof(Trip.NameChosen), "boolean", false);
        Assert.Equal(false, Prop<Trip>(nameof(Trip.NameChosen)).GetDefaultValue());
    }

    [Fact]
    public void Listing_ring_miss_streak_defaults_to_zero_with_a_check()
    {
        Column<TaxiListing>(nameof(TaxiListing.RingMissStreak), "smallint", false);
        Assert.Equal((short)0, Prop<TaxiListing>(nameof(TaxiListing.RingMissStreak)).GetDefaultValue());
        Assert.Equal("\"RingMissStreak\" >= 0", Check<TaxiListing>("ck_taxilistings_ringmiss"));
    }

    [Fact]
    public void Listing_rating_and_ring_ready_columns_exist_with_defaults()
    {
        foreach (var name in new[] { nameof(TaxiListing.RatingSum), nameof(TaxiListing.RatingCount), nameof(TaxiListing.RidesCompleted) })
        {
            Column<TaxiListing>(name, "integer", false);
            Assert.Equal(0, Prop<TaxiListing>(name).GetDefaultValue());
        }
        Column<TaxiListing>(nameof(TaxiListing.RingReadyAt), "timestamp with time zone", true);
        Column<TaxiListing>(nameof(TaxiListing.RingReadyBuild), "integer", true);
        Column<TaxiListing>(nameof(TaxiListing.RingNotificationsOn), "boolean", false);
        Assert.Equal(false, Prop<TaxiListing>(nameof(TaxiListing.RingNotificationsOn)).GetDefaultValue());
    }

    [Fact]
    public void Route_city_keys_and_their_index_exist()
    {
        Column<TaxiRoute>(nameof(TaxiRoute.FromCityKey), "bigint", true);
        Column<TaxiRoute>(nameof(TaxiRoute.ToCityKey), "bigint", true);
        var ix = Index<TaxiRoute>("ix_taxiroutes_to_from_city");
        AssertPlain(ix, ["ToCityKey", "FromCityKey"], "\"ToCityKey\" IS NOT NULL AND \"FromCityKey\" IS NOT NULL");
        Assert.Equal(["TaxiListingId"], ix.GetIncludeProperties()!.ToArray());
    }

    [Fact]
    public void No_btree_gist_extension_and_no_exclusion_constraint()
    {
        Assert.DoesNotContain(DesignModel.GetPostgresExtensions(), x => x.Name == "btree_gist");
        var taxiTypes = new[] { typeof(TaxiRequest), typeof(TaxiRequestOffer), typeof(TaxiBooking), typeof(TaxiRating),
            typeof(DriverDispatchState), typeof(TaxiDispatchCity), typeof(DueAction) };
        foreach (var type in taxiTypes)
        {
            var e = DesignModel.FindEntityType(type)!;
            Assert.DoesNotContain(e.GetCheckConstraints(), c => c.Sql.Contains("EXCLUDE", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(e.GetIndexes(), i => i.GetMethod() is "gist");
            Assert.DoesNotContain(e.GetProperties(), p => p.GetColumnType()?.Contains("range", StringComparison.OrdinalIgnoreCase) == true);
        }
    }
}
