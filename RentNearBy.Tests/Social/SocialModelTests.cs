using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using RentNearBy.Core.Entities.Buddy;
using RentNearBy.Core.Entities.Social;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Tests.Social;

// Contract 1: the social tables, indexes and checks exist in the EF model (the fresh InitialCreate is generated from it), and
// account deletion lists every new table.
public class SocialModelTests
{
    private static IModel Model()
    {
        using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=localhost;Database=design_only", o => o.UseNetTopologySuite())
            .Options);
        return db.GetService<IDesignTimeModel>().Model;
    }

    private static IEntityType Entity<T>(IModel model) => model.FindEntityType(typeof(T))!;

    private static readonly (Type Type, string Table)[] Tables =
    [
        (typeof(UserProfile), "UserProfiles"), (typeof(ProfileIdentityAudit), "ProfileIdentityAudit"), (typeof(Follow), "Follows"),
        (typeof(TripJoinRequest), "TripJoinRequests"), (typeof(ChatRoom), "ChatRooms"), (typeof(ChatRoomMember), "ChatRoomMembers"),
        (typeof(ChatMessage), "ChatMessages"), (typeof(MessageSnapshot), "MessageSnapshots"),
    ];

    [Fact]
    public void Every_social_table_is_mapped()
    {
        var model = Model();
        foreach (var (type, table) in Tables)
            Assert.Equal(table, model.FindEntityType(type)?.GetTableName());
    }

    private static void AssertIndexes(IEntityType e, params string[] names)
    {
        var have = e.GetIndexes().Select(i => i.GetDatabaseName()).ToHashSet();
        foreach (var n in names) Assert.Contains(n, have);
    }

    private static void AssertChecks(IEntityType e, params string[] names)
    {
        var have = e.GetCheckConstraints().Select(c => c.Name).ToHashSet();
        foreach (var n in names) Assert.Contains(n, have);
    }

    [Fact]
    public void Indexes_of_the_contract_exist()
    {
        var m = Model();
        AssertIndexes(Entity<Follow>(m), "ix_follows_following", "ix_follows_followers", "ix_follows_requests");
        AssertIndexes(Entity<TripJoinRequest>(m), "ux_tripjoinrequests_requester_client", "ux_tripjoinrequests_live", "ix_tripjoinrequests_trip",
            "ix_tripjoinrequests_requester", "ix_tripjoinrequests_ended");
        AssertIndexes(Entity<ChatRoom>(m), "ux_chatrooms_pair", "ux_chatrooms_trip", "ix_chatrooms_charged", "ix_chatrooms_preview");
        // The 10-minute preview purge reads only rooms that still show a preview (ChatExpiryPurgeJob.ClearPreviewSql).
        Assert.Equal("\"LastPreview\" IS NOT NULL", Entity<ChatRoom>(m).GetIndexes().Single(i => i.GetDatabaseName() == "ix_chatrooms_preview").GetFilter());
        AssertIndexes(Entity<ChatRoomMember>(m), "ix_chatmembers_list", "ix_chatmembers_unread");
        AssertIndexes(Entity<ChatMessage>(m), "ux_chatmessages_client", "ix_chatmessages_expiry");
        AssertIndexes(Entity<MessageSnapshot>(m), "ix_messagesnapshots_purge", "ix_messagesnapshots_report");
        AssertIndexes(Entity<ProfileIdentityAudit>(m), "ix_profileidentityaudit_user");
        AssertIndexes(Entity<Trip>(m), "ix_trips_public");
        Assert.True(Entity<ChatRoom>(m).GetIndexes().Single(i => i.GetDatabaseName() == "ux_chatrooms_pair").IsUnique);
        Assert.Equal("\"Status\" = 'Waiting'", Entity<TripJoinRequest>(m).GetIndexes().Single(i => i.GetDatabaseName() == "ux_tripjoinrequests_live").GetFilter());
    }

    [Fact]
    public void Checks_of_the_contract_exist()
    {
        var m = Model();
        AssertChecks(Entity<UserProfile>(m), "ck_userprofiles_gender", "ck_userprofiles_messagepolicy", "ck_userprofiles_followers", "ck_userprofiles_following");
        AssertChecks(Entity<Follow>(m), "ck_follows_not_self", "ck_follows_status");
        AssertChecks(Entity<TripJoinRequest>(m), "ck_tripjoinrequests_not_self", "ck_tripjoinrequests_status");
        AssertChecks(Entity<ChatRoom>(m), "ck_chatrooms_kind", "ck_chatrooms_status", "ck_chatrooms_pair");
        AssertChecks(Entity<ChatRoomMember>(m), "ck_chatmembers_bucket", "ck_chatmembers_unread", "ck_chatmembers_sent");
        AssertChecks(Entity<Trip>(m), "ck_trips_joingender", "ck_trips_public_fields", "ck_trips_posttravelmode");
        AssertChecks(Entity<TripMember>(m), "ck_tripmembers_joinedvia");
    }

    [Fact]
    public void Trip_and_member_carry_the_new_columns()
    {
        var m = Model();
        var trip = Entity<Trip>(m);
        foreach (var c in new[] { "IsPublic", "PublishedAt", "PostText", "PostDays", "PostWindowMonths", "PostStartMonth", "JoinGender", "Seats", "PostCityNames", "PostStateName", "PostTravelMode" })
            Assert.NotNull(trip.FindProperty(c));
        Assert.NotNull(Entity<TripMember>(m).FindProperty("JoinedVia"));
        Assert.NotNull(Entity<ListingReport>(m).FindProperty("MessageSeq"));
    }

    // Social next 1: one optional travel mode per post, a short code held to the four known values.
    [Fact]
    public void Trip_travel_mode_is_an_optional_checked_short_code()
    {
        var trip = Entity<Trip>(Model());
        var p = trip.FindProperty(nameof(Trip.PostTravelMode))!;
        Assert.True(p.IsNullable);
        Assert.Equal(RentNearBy.Core.Models.Social.PostTravelModes.MaxLength, p.GetMaxLength());
        Assert.Null(p.GetDefaultValueSql());
        var check = trip.GetCheckConstraints().Single(c => c.Name == "ck_trips_posttravelmode").Sql;
        Assert.Equal("\"PostTravelMode\" IS NULL OR \"PostTravelMode\" IN ('taxi', 'bike', 'bus', 'train')", check);
    }

    // Spec 23.9: the name rule column (nullable: null = never changed since onboarding) and the admin name audit.
    [Fact]
    public void Profile_carries_the_name_changed_at_column_and_the_audit_the_name()
    {
        var m = Model();
        var changedAt = Entity<UserProfile>(m).FindProperty(nameof(UserProfile.NameChangedAt));
        Assert.NotNull(changedAt);
        Assert.True(changedAt!.IsNullable);
        Assert.Null(changedAt.GetDefaultValueSql());
        var audit = Entity<ProfileIdentityAudit>(m);
        foreach (var c in new[] { nameof(ProfileIdentityAudit.OldName), nameof(ProfileIdentityAudit.NewName) })
        {
            var p = audit.FindProperty(c)!;
            Assert.True(p.IsNullable);
            Assert.Equal(RentNearBy.Core.Models.Social.SocialLimits.NameMax, p.GetMaxLength());
        }
    }

    [Fact]
    public void Profile_and_follows_cascade_with_the_user()
    {
        var m = Model();
        Assert.Contains(Entity<UserProfile>(m).GetForeignKeys(), f => f.PrincipalEntityType.ClrType == typeof(User) && f.DeleteBehavior == DeleteBehavior.Cascade);
        Assert.Equal(2, Entity<Follow>(m).GetForeignKeys().Count(f => f.PrincipalEntityType.ClrType == typeof(User) && f.DeleteBehavior == DeleteBehavior.Cascade));
    }

    [Fact]
    public void Account_deletion_lists_every_new_table()
    {
        var listed = AccountDeletionCoverage.Tables.Select(t => t.Table).ToHashSet();
        foreach (var (_, table) in Tables) Assert.Contains(table, listed);
    }
}
