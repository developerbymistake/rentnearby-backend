using System.Reflection;
using System.Text.Json;
using RentNearBy.Core.DTOs.Social;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Infrastructure.Services.Social;

namespace RentNearBy.Tests.Social;

// Travel levels (primary blue + levels spec B) and the level-up congratulation (spec C): pure table, copy, key and the
// source guards of the SQL behind the level fields.
public class TravelLevelsTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(9, 1)]
    [InlineData(10, 2)]
    [InlineData(29, 2)]
    [InlineData(30, 3)]
    [InlineData(79, 3)]
    [InlineData(80, 4)]
    [InlineData(199, 4)]
    [InlineData(200, 5)]
    [InlineData(499, 5)]
    [InlineData(500, 6)]
    [InlineData(10000, 6)]
    public void Level_follows_the_table_at_every_boundary(int trips, int level) => Assert.Equal(level, TravelLevels.Level(trips));

    [Fact]
    public void A_negative_count_reads_as_level_zero() => Assert.Equal(0, TravelLevels.Level(-3));

    [Fact]
    public void The_table_is_the_decided_one()
    {
        var expected = new (int Index, int Min, int? Max, string Name)[]
        {
            (0, 0, 0, "New Traveller"), (1, 1, 9, "Trail Starter"), (2, 10, 29, "Wanderer"), (3, 30, 79, "Trekker"),
            (4, 80, 199, "Pathfinder"), (5, 200, 499, "Summit Seeker"), (6, 500, null, "Travel King"),
        };
        Assert.Equal(expected, TravelLevels.All.Select(l => (l.Index, l.Min, l.Max, l.Name)));
        Assert.Equal(TravelLevels.All.Count - 1, TravelLevels.Highest);
        for (var i = 1; i < TravelLevels.All.Count; i++) Assert.Equal(TravelLevels.All[i - 1].Max + 1, TravelLevels.All[i].Min);
    }

    [Fact]
    public void Only_the_six_first_numbers_reach_a_level()
    {
        var hits = Enumerable.Range(-1, 1200).Where(n => TravelLevels.ReachedAt(n) != null).ToList();
        Assert.Equal([1, 10, 30, 80, 200, 500], hits);
        Assert.Equal([1, 2, 3, 4, 5, 6], hits.Select(n => TravelLevels.ReachedAt(n)!.Value));
    }

    [Fact]
    public void Copy_of_every_level_is_exactly_the_spec()
    {
        var id = Guid.NewGuid();
        var c1 = LevelUpPushCopy.For(id, 1, 1, "Rahul Verma");
        Assert.Equal("Your first trip is done", c1.Title);
        Assert.Equal("Congratulations, Rahul! You completed your first trip and reached Trail Starter. Keep exploring.", c1.Body);

        foreach (var (level, n, name) in new[] { (2, 10, "Wanderer"), (3, 30, "Trekker"), (4, 80, "Pathfinder"), (5, 200, "Summit Seeker") })
        {
            var c = LevelUpPushCopy.For(id, level, n, "Rahul Verma");
            Assert.Equal("New level reached", c.Title);
            Assert.Equal($"Congratulations, Rahul! You are now a {name} with {n} trips completed. Keep exploring.", c.Body);
        }

        var c6 = LevelUpPushCopy.For(id, 6, 500, "Rahul Verma");
        Assert.Equal("You are the Travel King", c6.Title);
        Assert.Equal("Congratulations, Rahul! 500 trips completed. You have reached the highest level.", c6.Body);
    }

    [Fact]
    public void Copy_without_a_name_has_no_placeholder()
    {
        var c = LevelUpPushCopy.For(Guid.NewGuid(), 2, 10, "  ");
        Assert.Equal("Congratulations! You are now a Wanderer with 10 trips completed. Keep exploring.", c.Body);
    }

    [Fact]
    public void Key_is_one_per_user_and_level_and_maps_to_one_inbox_row()
    {
        var id = Guid.NewGuid();
        Assert.Equal($"levelup:{id:N}:3", LevelUpPushCopy.Key(id, 3));
        Assert.Equal(LevelUpPushCopy.Key(id, 3), LevelUpPushCopy.For(id, 3, 30, "A").Key);
        Assert.Equal(LevelUpNotice.RowId(LevelUpPushCopy.Key(id, 3)), LevelUpNotice.RowId(LevelUpPushCopy.Key(id, 3)));
        Assert.NotEqual(LevelUpNotice.RowId(LevelUpPushCopy.Key(id, 3)), LevelUpNotice.RowId(LevelUpPushCopy.Key(id, 4)));
    }

    [Fact]
    public void Only_an_active_seeker_at_a_first_number_gets_a_congratulation()
    {
        var id = Guid.NewGuid();
        Assert.NotNull(LevelUpNotice.CopyFor(id, 10, "Asha", isActiveSeeker: true));
        Assert.Null(LevelUpNotice.CopyFor(id, 10, "Asha", isActiveSeeker: false));
        Assert.Null(LevelUpNotice.CopyFor(id, 11, "Asha", isActiveSeeker: true));
        Assert.Null(LevelUpNotice.CopyFor(id, 0, "Asha", isActiveSeeker: true));
    }

    [Fact]
    public void Push_opens_the_travel_levels_page_and_carries_the_inbox_row()
    {
        var id = Guid.NewGuid();
        var copy = LevelUpPushCopy.For(id, 2, 10, "Asha");
        var rowId = LevelUpNotice.RowId(copy.Key);
        var p = JsonSerializer.Deserialize<OutboxPushPayload>(LevelUpNotice.Payload(id, rowId, copy), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(copy.Key, p.Key);
        Assert.Equal(id, p.UserId);
        Assert.Equal(rowId, p.NotificationId);
        Assert.Equal(copy.Title, p.Title);
        Assert.Equal(copy.Body, p.Body);
        Assert.Equal("/travel-levels", p.Route);
        Assert.Equal("{}", p.ArgsJson);
        Assert.Equal(NotificationTypes.TravelLevelUp, p.Kind);
        Assert.Equal("travel_level_up", NotificationTypes.ToWireValue(NotificationTypes.TravelLevelUp));
        Assert.True(TravellerOnly.IsTravellerPush(NotificationTypes.TravelLevelUp, UserNotificationRoutes.TravelLevels));
    }

    [Fact]
    public void Write_is_idempotent_by_key_and_row_id()
    {
        Assert.Contains("ON CONFLICT (\"Id\") DO NOTHING", LevelUpNotice.WriteSql);
        Assert.Contains("ON CONFLICT (\"Key\") DO NOTHING", LevelUpNotice.WriteSql);
        Assert.Contains("\"NotificationEvents\"", LevelUpNotice.WriteSql);
        Assert.Contains("\"OutboxMessages\"", LevelUpNotice.WriteSql);
        Assert.DoesNotContain("{", LevelUpNotice.WriteSql);
    }

    [Fact]
    public void Only_the_seeker_counter_returns_the_new_count()
    {
        Assert.Contains("RETURNING u.\"Id\", u.\"CompletedTripsCount\"", TripCompletionCounter.TripSql);
        Assert.DoesNotContain("RETURNING u.", TripCompletionCounter.HostsSql);
        Assert.DoesNotContain("RETURNING u.", TripCompletionCounter.TaxiBookingSql);
        var src = File.ReadAllText(Path.Combine(RentNearBy.Tests.Trips.CompletionCounterGuardTests.Root(), "RentNearBy.Infrastructure", "Services", "TripCompletionCounter.cs"));
        Assert.Equal(1, Count(src, "LevelUpNotice.WriteInTxAsync"));
        Assert.Contains("UserRoles.SeekerId", src);
    }

    // ---------- source guards: every level DTO reads Users.CompletedTripsCount ----------

    private static string Field(Type t, string name)
        => (string)t.GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;

    [Fact]
    public void My_profile_and_traveller_profile_select_the_completed_trips_count()
    {
        Assert.Contains("u.\"CompletedTripsCount\"", Field(typeof(SocialProfileService), "MeSql"));
        Assert.Contains("u.\"CompletedTripsCount\"", Field(typeof(SocialProfileService), "TravellerSql"));
    }

    [Fact]
    public void Public_trip_owner_selects_the_completed_trips_count()
    {
        var select = typeof(PublicTripReader).GetMethod("SelectSql", BindingFlags.NonPublic | BindingFlags.Static)!;
        var sql = (string)select.Invoke(null, ["TRUE", "ORDER BY 1"])!;
        Assert.Contains("o.\"CompletedTripsCount\" AS \"OwnerCompletedTrips\"", sql);
    }

    [Fact]
    public void Join_request_list_selects_the_requester_completed_trips_count()
        => Assert.Contains("u.\"CompletedTripsCount\" AS \"CompletedTrips\"", Field(typeof(JoinRequestService), "ListSql"));

    [Fact]
    public void Profile_trips_counter_is_completed_trips_only()
    {
        var src = File.ReadAllText(Path.Combine(RentNearBy.Tests.Trips.CompletionCounterGuardTests.Root(), "RentNearBy.Infrastructure", "Services", "Social", "SocialProfileService.cs"));
        Assert.Contains("Trips = row.CompletedTripsCount, Followers", src);
        Assert.Contains("Trips = r.CompletedTripsCount, Followers", src);
        Assert.DoesNotContain("CompletedTripsCount + ", src);
    }

    [Fact]
    public void Person_level_fields_are_left_out_when_not_filled_and_filled_together()
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var bare = JsonSerializer.Serialize(new SocialPersonDto { Name = "A" }, web);
        Assert.DoesNotContain("completedTrips", bare);
        Assert.DoesNotContain("\"level\"", bare);

        var filled = SocialSql.WithLevel(new PersonRowDto { Name = "A" }, 30);
        Assert.Equal(30, filled.CompletedTrips);
        Assert.Equal(3, filled.Level);
        var json = JsonSerializer.Serialize<SocialPersonDto>(filled, web);
        Assert.Contains("\"completedTrips\":30", json);
        Assert.Contains("\"level\":3", json);
        var negative = SocialSql.WithLevel(new SocialPersonDto(), -1);
        Assert.Equal(0, negative.CompletedTrips);
        Assert.Equal(0, negative.Level);
    }

    [Fact]
    public void My_profile_always_carries_the_level_fields()
    {
        var json = JsonSerializer.Serialize(new MyProfileDto { CompletedTrips = 0, Level = 0 }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"completedTrips\":0", json);
        Assert.Contains("\"level\":0", json);
    }

    private static int Count(string text, string part)
    {
        var n = 0;
        for (var i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal)) n++;
        return n;
    }
}
