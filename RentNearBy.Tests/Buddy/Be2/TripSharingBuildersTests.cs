using RentNearBy.Api.Validators.Buddy;
using RentNearBy.Core.DTOs.Buddy;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Services.Buddy.Be2;
using RentNearBy.Infrastructure.Services.Retention;
using RentNearBy.Infrastructure.Services.Retention.Buddy;

namespace RentNearBy.Tests.Buddy.Be2;

public class TripSharingBuildersTests
{
    private static readonly Guid TripId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly Guid Rahul = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid Aman = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid Priya = Guid.Parse("33333333-3333-4333-8333-333333333333");
    private static readonly Guid Neha = Guid.Parse("44444444-4444-4444-8444-444444444444");
    private static readonly DateTime Now = new(2026, 5, 18, 6, 0, 0, DateTimeKind.Utc);

    private static TripAccessInfo Info(TripAccessLevel level) => new(
        TripId, Rahul, "Rahul", "Uttarakhand Hills Trip", TripStatuses.Planned, new DateOnly(2026, 5, 16), new DateOnly(2026, 5, 22),
        7, 18, null, Now, level, null, null, null);

    private static List<TripMemberService.PersonRow> Rows() =>
    [
        new() { UserId = Rahul, Name = "Rahul", Username = "Bakhli#48291736", Kind = "Owner" },
        new() { UserId = Aman, Name = "Aman", Username = "Bakhli#61408275", Kind = "Joined", JoinedAt = Now.AddDays(-2), IsBuddy = false },
        new() { UserId = Priya, Name = "Priya", Username = "Bakhli#50267139", Kind = "Joined", JoinedAt = Now, IsBuddy = false },
        new() { UserId = Neha, Name = "Neha", Username = "Bakhli#73019462", Kind = "Invited", MemberId = Guid.NewGuid(), InvitedAt = Now, InviteExpiresAt = Now.AddDays(4) },
    ];

    [Fact]
    public void Owner_members_match_sample_3_12()
    {
        var dto = TripMemberService.BuildMembers(Info(TripAccessLevel.Owner), Rows(), Rahul, Now);
        Assert.Equal(("Owner", 3, 1, 8, 12), (dto.Role, dto.TravellerCount, dto.PendingInvites, dto.SeatsLeft, dto.MaxTravellers));
        Assert.Equal([Rahul, Aman, Priya], dto.Joined.Select(j => j.UserId));
        Assert.All(dto.Joined, j => Assert.NotNull(j.Username));
        Assert.Null(dto.Joined[0].JoinedAt);
        Assert.Equal("Owner", dto.Joined[0].Role);
        Assert.Equal("1736", dto.Joined[0].Last4);
        Assert.True(dto.Joined[0].IsMe);
        Assert.Single(dto.Invited);
        Assert.True(dto.CanInvite);
        Assert.False(dto.CanLeave);
    }

    [Fact]
    public void Viewer_members_hide_invites_and_seats_and_see_public_usernames()
    {
        var dto = TripMemberService.BuildMembers(Info(TripAccessLevel.Viewer), Rows(), Aman, Now);
        Assert.Equal(("Viewer", 0, 0), (dto.Role, dto.PendingInvites, dto.SeatsLeft));
        Assert.Empty(dto.Invited);
        Assert.Equal("Bakhli#48291736", dto.Joined[0].Username);
        Assert.Equal("Bakhli#61408275", dto.Joined[1].Username);
        Assert.Equal("Bakhli#50267139", dto.Joined[2].Username);
        Assert.Equal("7139", dto.Joined[2].Last4);
        Assert.False(dto.CanInvite);
        Assert.True(dto.CanLeave);
    }

    [Fact]
    public void Ended_invite_data_names_the_trip_only_for_a_known_reason()
    {
        var row = new InviteRow { Status = TripMemberStatuses.Cancelled, InviteExpiresAt = Now.AddDays(1), Title = "Uttarakhand Hills Trip", OwnerName = "Rahul" };
        var data = TripInviteService.EndedData(row, Now);
        Assert.Equal(("Cancelled", "Uttarakhand Hills Trip", "Rahul"), (data.Reason, data.Title, data.OwnerName));
        row.Blocked = true;
        Assert.Equal((InviteEndedReasons.Unknown, (string?)null), (TripInviteService.EndedData(row, Now).Reason, TripInviteService.EndedData(row, Now).Title));
        Assert.Equal(InviteEndedReasons.Unknown, TripInviteService.EndedData(null, Now).Reason);
        var left = new InviteRow { Status = TripMemberStatuses.Left, InviteExpiresAt = Now, Title = "T", OwnerName = "R" };
        Assert.Null(TripInviteService.EndedData(left, Now).OwnerName);
    }

    [Fact]
    public void Leg_cities_fall_back_to_the_trip_city()
    {
        Assert.Equal(["Nainital"], Be2Sql.LegCities("{}", "Nainital"));
        Assert.Equal(["Nainital"], Be2Sql.LegCities("not json", "Nainital"));
        Assert.Equal(["A", "B"], Be2Sql.LegCities("""{"legs":[{"index":1,"cityName":"A"},{"index":2,"cityName":"B"}]}""", "X"));
    }

    [Fact]
    public void Shared_list_state_accepts_only_upcoming_and_past()
    {
        Assert.True(SharedListStates.IsValid("upcoming"));
        Assert.True(SharedListStates.IsValid("past"));
        Assert.False(SharedListStates.IsValid("all"));
        Assert.False(SharedListStates.IsValid(null));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(11, true)]
    [InlineData(12, false)]
    public void Invite_validator_takes_1_to_11_ids(int count, bool valid)
    {
        var request = new TripInviteRequest { ClientRequestId = Guid.NewGuid(), UserIds = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToList() };
        Assert.Equal(valid, new TripInviteRequestValidator().Validate(request).IsValid);
    }

    [Fact]
    public void Invite_validator_refuses_duplicates_empty_ids_and_no_client_request_id()
    {
        var v = new TripInviteRequestValidator();
        Assert.False(v.Validate(new TripInviteRequest { ClientRequestId = Guid.NewGuid(), UserIds = [Aman, Aman] }).IsValid);
        Assert.False(v.Validate(new TripInviteRequest { ClientRequestId = Guid.NewGuid(), UserIds = [Guid.Empty] }).IsValid);
        Assert.False(v.Validate(new TripInviteRequest { UserIds = [Aman] }).IsValid);
    }

    [Fact]
    public void Retention_rule_deletes_only_ended_invites_after_90_days()
    {
        var def = Assert.Single(Be2RetentionRules.Definitions);
        Assert.Equal(("TripMemberInvites", "TripMembers", 90, RetentionGroups.TripHousekeeping), (def.Name, def.Table, def.DefaultDays, def.Group));
        var sql = new DeclarativeRetentionRule(def).BuildDeleteSql();
        Assert.Contains("'Declined', 'Cancelled', 'Expired'", sql);
        Assert.DoesNotContain("'Joined'", sql);
        Assert.Contains("FOR UPDATE SKIP LOCKED", sql);
    }

    [Fact]
    public void Expiry_job_is_hourly()
    {
        var job = new TripInviteExpiryJob(null!);
        Assert.Equal(("TripInviteExpiry", TimeSpan.FromHours(1), (TimeOnly?)null), (job.Name, job.Interval, job.DailyAtIst));
    }
}
