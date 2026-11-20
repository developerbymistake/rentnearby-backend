using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using RentNearBy.Core.Entities.Buddy;
using RentNearBy.Core.Entities.TaxiRequests;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Tests.Buddy.Foundation;

// CONTRACT_BACKEND section 1: every table, column, check and index name of the feature, pinned against the EF model.
public class BuddyModelTests
{
    private static ApplicationDbContext NewContext() => new(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseNpgsql("Host=localhost;Database=design_only", o => o.UseNetTopologySuite())
        .Options);

    private static readonly IModel Model = BuildModel();

    private static IModel BuildModel()
    {
        using var db = NewContext();
        return db.GetService<IDesignTimeModel>().Model;
    }

    private static IEntityType Entity<T>() => Model.FindEntityType(typeof(T)) ?? throw new Xunit.Sdk.XunitException($"No entity {typeof(T).Name}");

    private static IIndex Index<T>(string name)
    {
        var matches = Entity<T>().GetIndexes().Where(i => i.GetDatabaseName() == name).ToList();
        Assert.True(matches.Count == 1, $"{typeof(T).Name} has {matches.Count} indexes named {name}");
        return matches[0];
    }

    private static void AssertIndex<T>(string name, string[] columns, bool unique = false, string? filter = null, bool[]? descending = null, string[]? include = null)
    {
        var ix = Index<T>(name);
        Assert.Equal(columns, ix.Properties.Select(p => p.Name).ToArray());
        Assert.Equal(unique, ix.IsUnique);
        Assert.Equal(filter, ix.GetFilter());
        if (descending != null) Assert.Equal(descending, ix.IsDescending);
        var included = ix.FindAnnotation("Npgsql:IndexInclude")?.Value as IEnumerable<string>;
        Assert.Equal(include ?? [], included?.ToArray() ?? []);
    }

    private static void AssertChecks<T>(params string[] names)
    {
        var have = Entity<T>().GetCheckConstraints().Select(c => c.Name).ToHashSet();
        foreach (var n in names) Assert.True(have.Contains(n), $"{typeof(T).Name} has no check {n}");
    }

    [Theory]
    [InlineData(typeof(BuddyRequest), "BuddyRequests")]
    [InlineData(typeof(BuddyLink), "Buddies")]
    [InlineData(typeof(TripMember), "TripMembers")]
    [InlineData(typeof(TripExpense), "TripExpenses")]
    [InlineData(typeof(TripExpenseShare), "TripExpenseShares")]
    [InlineData(typeof(TripExpenseRevision), "TripExpenseRevisions")]
    [InlineData(typeof(TripPayment), "TripPayments")]
    [InlineData(typeof(TripExpenseSuggestionSkip), "TripExpenseSuggestionSkips")]
    [InlineData(typeof(TripExpenseState), "TripExpenseState")]
    [InlineData(typeof(UserNotice), "UserNotices")]
    public void New_table_exists(Type type, string table)
        => Assert.Equal(table, Model.FindEntityType(type)!.GetTableName());

    [Fact]
    public void BuddyRequests_shape()
    {
        AssertChecks<BuddyRequest>("ck_buddyrequests_status", "ck_buddyrequests_pair", "ck_buddyrequests_not_self", "ck_buddyrequests_source");
        AssertIndex<BuddyRequest>("ux_buddyrequests_pair_open", ["PairLowId", "PairHighId"], unique: true, filter: "\"Status\" = 'Pending'");
        AssertIndex<BuddyRequest>("ux_buddyrequests_from_client", ["FromUserId", "ClientRequestId"], unique: true);
        AssertIndex<BuddyRequest>("ix_buddyrequests_to_pending", ["ToUserId", "CreatedAt", "Id"], filter: "\"Status\" = 'Pending'", descending: [false, true, true]);
        AssertIndex<BuddyRequest>("ix_buddyrequests_from_created", ["FromUserId", "CreatedAt", "Id"], descending: [false, true, true]);
        AssertIndex<BuddyRequest>("ix_buddyrequests_pair_created", ["PairLowId", "PairHighId", "CreatedAt"], descending: [false, false, true]);
        AssertIndex<BuddyRequest>("ix_buddyrequests_pending_expires", ["ExpiresAt"], filter: "\"Status\" = 'Pending'");
        AssertIndex<BuddyRequest>("ix_buddyrequests_ended", ["EndedAt"], filter: "\"EndedAt\" IS NOT NULL");
        Assert.Equal(20, Entity<BuddyRequest>().FindProperty("Status")!.GetMaxLength());
        Assert.Equal(10, Entity<BuddyRequest>().FindProperty("Source")!.GetMaxLength());
    }

    [Fact]
    public void Buddies_shape()
    {
        Assert.Equal(["UserId", "BuddyUserId"], Entity<BuddyLink>().FindPrimaryKey()!.Properties.Select(p => p.Name).ToArray());
        AssertChecks<BuddyLink>("ck_buddies_not_self");
        AssertIndex<BuddyLink>("ix_buddies_buddy", ["BuddyUserId"]);
        AssertIndex<BuddyLink>("ix_buddies_user_unseen", ["UserId"], filter: "\"SeenAt\" IS NULL");
    }

    [Fact]
    public void TripMembers_shape()
    {
        AssertChecks<TripMember>("ck_tripmembers_status");
        AssertIndex<TripMember>("ux_tripmembers_trip_user", ["TripId", "UserId"], unique: true);
        AssertIndex<TripMember>("ix_tripmembers_trip_status", ["TripId", "Status"]);
        AssertIndex<TripMember>("ix_tripmembers_user_joined", ["UserId", "JoinedAt", "Id"], filter: "\"Status\" = 'Joined'", descending: [false, true, true]);
        AssertIndex<TripMember>("ix_tripmembers_user_status", ["UserId", "Status"]);
        AssertIndex<TripMember>("ix_tripmembers_invite_expires", ["InviteExpiresAt"], filter: "\"Status\" = 'Invited'");
        AssertIndex<TripMember>("ix_tripmembers_ended", ["EndedAt"], filter: "\"Status\" IN ('Declined', 'Cancelled', 'Expired')");
    }

    [Fact]
    public void TripExpenses_shape()
    {
        AssertChecks<TripExpense>("ck_tripexpenses_amount", "ck_tripexpenses_category", "ck_tripexpenses_splitmode",
            "ck_tripexpenses_day", "ck_tripexpenses_link", "ck_tripexpenses_linkkind");
        AssertIndex<TripExpense>("ix_tripexpenses_trip_day_live", ["TripId", "DayNumber", "CreatedAt", "Id"],
            filter: "\"DeletedAt\" IS NULL", descending: [false, true, true, true], include: ["Amount", "PaidByUserId"]);
        AssertIndex<TripExpense>("ix_tripexpenses_trip_link", ["TripId", "LinkKind", "LinkId"],
            filter: "\"LinkId\" IS NOT NULL AND \"DeletedAt\" IS NULL", include: ["Amount"]);
        Assert.Equal(ValueGenerated.Never, Entity<TripExpense>().FindProperty("Id")!.ValueGenerated);
        Assert.Equal(60, Entity<TripExpense>().FindProperty("Title")!.GetMaxLength());
        Assert.Equal(200, Entity<TripExpense>().FindProperty("Note")!.GetMaxLength());
    }

    [Fact]
    public void TripExpenseShares_and_revisions_shape()
    {
        Assert.Equal(["ExpenseId", "UserId"], Entity<TripExpenseShare>().FindPrimaryKey()!.Properties.Select(p => p.Name).ToArray());
        AssertChecks<TripExpenseShare>("ck_tripexpenseshares_share");
        AssertIndex<TripExpenseShare>("ix_tripexpenseshares_trip_user", ["TripId", "UserId"], include: ["Share"]);

        AssertChecks<TripExpenseRevision>("ck_tripexpenserevisions_kind");
        AssertIndex<TripExpenseRevision>("ux_tripexpenserevisions_expense_version", ["ExpenseId", "Version"], unique: true);
        AssertIndex<TripExpenseRevision>("ux_tripexpenserevisions_expense_op", ["ExpenseId", "OpId"], unique: true);
        Assert.Equal("jsonb", Entity<TripExpenseRevision>().FindProperty("ChangesJson")!.GetColumnType());
        Assert.Equal("jsonb", Entity<TripExpenseRevision>().FindProperty("SharesAfterJson")!.GetColumnType());
    }

    [Fact]
    public void TripPayments_shape()
    {
        AssertChecks<TripPayment>("ck_trippayments_state", "ck_trippayments_not_self", "ck_trippayments_amount",
            "ck_trippayments_received", "ck_trippayments_ended");
        AssertIndex<TripPayment>("ix_trippayments_trip_state", ["TripId", "State"], include: ["FromUserId", "ToUserId", "Amount"]);
        AssertIndex<TripPayment>("ux_trippayments_pair_saidpaid", ["TripId", "FromUserId", "ToUserId"], unique: true, filter: "\"State\" = 'SaidPaid'");
        AssertIndex<TripPayment>("ix_trippayments_ended", ["EndedAt"], filter: "\"EndedAt\" IS NOT NULL");
        Assert.Equal(11, Entity<TripPayment>().FindProperty("State")!.GetMaxLength());
    }

    [Fact]
    public void State_skip_and_notice_shape()
    {
        Assert.Equal(["TripId", "SourceKind", "SourceId"], Entity<TripExpenseSuggestionSkip>().FindPrimaryKey()!.Properties.Select(p => p.Name).ToArray());
        Assert.Equal(["TripId"], Entity<TripExpenseState>().FindPrimaryKey()!.Properties.Select(p => p.Name).ToArray());

        AssertChecks<UserNotice>("ck_usernotices_kind", "ck_usernotices_count");
        AssertIndex<UserNotice>("ux_usernotices_user_kind_trip", ["UserId", "Kind", "TripId"], unique: true);
        AssertIndex<UserNotice>("ix_usernotices_user_updated", ["UserId", "UpdatedAt", "Id"], descending: [false, true, true]);
        AssertIndex<UserNotice>("ix_usernotices_user_unread", ["UserId"], filter: "\"ReadAt\" IS NULL");
        AssertIndex<UserNotice>("ix_usernotices_trip_kind", ["TripId", "Kind"]);
        AssertIndex<UserNotice>("ix_usernotices_updated", ["UpdatedAt"]);
    }

    [Fact]
    public void UserNotice_holds_no_text_but_the_kind()
    {
        var strings = typeof(UserNotice).GetProperties().Where(p => p.PropertyType == typeof(string)).Select(p => p.Name).ToArray();
        Assert.Equal(["Kind"], strings);
    }

    [Fact]
    public void Changed_existing_tables()
    {
        Assert.NotNull(Entity<User>().FindProperty("BuddyRequestsHeldUntil"));

        AssertChecks<UserBlock>("ck_userblocks_not_self");
        AssertIndex<UserBlock>("ix_userblocks_blocker_created", ["BlockerId", "CreatedAt", "Id"], descending: [false, true, true]);
        AssertIndex<UserBlock>("ix_userblocks_blocked", ["BlockedId"]);
        Assert.Single(Entity<UserBlock>().GetIndexes(), i => i.IsUnique && i.Properties.Select(p => p.Name).SequenceEqual(["BlockerId", "BlockedId"]));

        AssertChecks<ReportReason>("ck_reportreasons_scope");
        AssertIndex<ReportReason>("ix_reportreasons_scope_name", ["Scope", "Name"], unique: true);
        Assert.DoesNotContain(Entity<ReportReason>().GetIndexes(), i => i.IsUnique && i.Properties.Select(p => p.Name).SequenceEqual(["Name"]));
        Assert.Equal(10, Entity<ReportReason>().FindProperty("Scope")!.GetMaxLength());

        Assert.Equal(24, Entity<ListingReport>().FindProperty("ReportedUsername")!.GetMaxLength());
        Assert.Equal(10, Entity<ListingReport>().FindProperty("Context")!.GetMaxLength());
        AssertIndex<ListingReport>("ix_listingreports_user_reported", ["ReportedUserId", "CreatedAt"],
            filter: "\"ListingType\" = 'User'", descending: [false, true]);

        Assert.Equal(24, Entity<DeletedAccountRecord>().FindProperty("Username")!.GetMaxLength());
        AssertIndex<DeletedAccountRecord>("ix_deletedaccountrecords_username", ["Username"], filter: "\"Username\" IS NOT NULL");

        AssertIndex<TaxiBooking>("ix_taxibookings_trip", ["TripId"]);
    }

    [Fact]
    public void Trips_has_no_new_column()
    {
        var columns = Entity<Trip>().GetProperties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        string[] expected =
        [
            "CityKey", "CityLatitude", "CityLongitude", "CityName", "ClientTripId", "CompletedAt", "CreatedAt", "DayCount",
            "DistrictId", "EndDate", "Id", "InviteCode", "MutedAt", "NameChosen", "OwnerUserId", "PlanJson", "SpotCount",
            "StartDate", "Status", "Title", "UpdatedAt", "xmin",
        ];
        Assert.Equal(expected.OrderBy(n => n, StringComparer.Ordinal), columns);
    }

    [Fact]
    public void Exactly_one_migration_and_it_has_every_buddy_table_and_index()
    {
        using var db = NewContext();
        var assembly = db.GetService<IMigrationsAssembly>();
        var id = Assert.Single(assembly.Migrations).Key;
        Assert.EndsWith("_InitialCreate", id);

        var snapshot = assembly.ModelSnapshot!.Model;
        if (snapshot is IMutableModel mutable) snapshot = mutable.FinalizeModel();
        snapshot = db.GetService<IModelRuntimeInitializer>().Initialize(snapshot, designTime: true, validationLogger: null);
        var tables = snapshot.GetRelationalModel().Tables.ToDictionary(t => t.Name);

        string[] newTables =
        [
            "BuddyRequests", "Buddies", "TripMembers", "TripExpenses", "TripExpenseShares", "TripExpenseRevisions",
            "TripPayments", "TripExpenseSuggestionSkips", "TripExpenseState", "UserNotices",
        ];
        foreach (var t in newTables) Assert.True(tables.ContainsKey(t), $"Snapshot has no table {t}");

        var modelIndexNames = Model.GetEntityTypes()
            .Where(e => e.ClrType.Namespace == typeof(BuddyLink).Namespace)
            .SelectMany(e => e.GetIndexes()).Select(i => i.GetDatabaseName()!).ToList();
        string[] changedIndexes =
        [
            "ix_userblocks_blocker_created", "ix_userblocks_blocked", "ix_reportreasons_scope_name", "ix_listingreports_user_reported",
            "ix_deletedaccountrecords_username", "ix_taxibookings_trip",
        ];
        var snapshotIndexNames = tables.Values.SelectMany(t => t.Indexes).Select(i => i.Name).ToHashSet();
        foreach (var name in modelIndexNames.Where(n => n.StartsWith("ix_") || n.StartsWith("ux_")).Concat(changedIndexes))
            Assert.True(snapshotIndexNames.Contains(name), $"Snapshot has no index {name}");
    }
}
