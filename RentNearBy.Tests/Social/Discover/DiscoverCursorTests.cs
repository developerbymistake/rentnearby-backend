using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using RentNearBy.Core.Entities.Social;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Tests.Social.Discover;

// Discover cursor (DESIGN 1.4) and the Discover schema in the EF model (DESIGN 8; the fresh InitialCreate is generated from it).
public class DiscoverCursorTests
{
    private static readonly Guid U = Guid.Parse("0123456789abcdef0123456789abcdef");

    private static string B64(string raw) => Convert.ToBase64String(Encoding.ASCII.GetBytes(raw)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public void Cursor_round_trips_and_is_url_safe()
    {
        var text = DiscoverCursor.Encode(1764300000123, 145, U);
        Assert.False(text.Contains('+'));
        Assert.False(text.Contains('/'));
        Assert.False(text.Contains('='));
        Assert.True(DiscoverCursor.TryDecode(text, out var c));
        Assert.Equal(new DiscoverCursorValue(1764300000123, 145, U), c);
        Assert.Equal(DiscoverRules.ZScore(145, U), c.ZScore);
        Assert.Equal(text, DiscoverCursor.Encode(c));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("!!!")]
    [InlineData("abc")]
    public void Unreadable_cursors_are_refused(string? cursor) => Assert.False(DiscoverCursor.TryDecode(cursor, out _));

    [Fact]
    public void Wrong_shapes_are_refused()
    {
        Assert.False(DiscoverCursor.TryDecode(B64("1|2"), out _));
        Assert.False(DiscoverCursor.TryDecode(B64("x|2|0123456789abcdef0123456789abcdef"), out _));
        Assert.False(DiscoverCursor.TryDecode(B64("1|-2|0123456789abcdef0123456789abcdef"), out _));
        Assert.False(DiscoverCursor.TryDecode(B64("1|1024|0123456789abcdef0123456789abcdef"), out _));
        Assert.False(DiscoverCursor.TryDecode(B64("1|2|not-a-guid"), out _));
        Assert.True(DiscoverCursor.TryDecode(B64("1|1023|0123456789abcdef0123456789abcdef"), out _));
    }

    [Fact]
    public void A_new_snapshot_stamp_still_continues_on_the_score()
    {
        Assert.True(DiscoverCursor.TryDecode(DiscoverCursor.Encode(1, 50, U), out var a));
        Assert.True(DiscoverCursor.TryDecode(DiscoverCursor.Encode(2, 50, U), out var b));
        Assert.NotEqual(a.Stamp, b.Stamp);
        Assert.Equal(a.ZScore, b.ZScore);
    }

    private static IModel Model()
    {
        using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=localhost;Database=design_only", o => o.UseNetTopologySuite())
            .Options);
        return db.GetService<IDesignTimeModel>().Model;
    }

    private static IIndex Index<T>(IModel m, string name) => m.FindEntityType(typeof(T))!.GetIndexes().Single(i => i.GetDatabaseName() == name);

    [Fact]
    public void Discover_tables_columns_and_indexes_are_in_the_model()
    {
        var m = Model();
        Assert.Equal("DiscoverHides", m.FindEntityType(typeof(DiscoverHide))!.GetTableName());
        Assert.Equal("FollowCountDirty", m.FindEntityType(typeof(FollowCountDirty))!.GetTableName());
        Assert.Equal("FollowPushGroups", m.FindEntityType(typeof(FollowPushGroup))!.GetTableName());

        var profile = m.FindEntityType(typeof(UserProfile))!;
        Assert.Equal("'{}'::timestamptz[]", profile.FindProperty(nameof(UserProfile.FollowSendTimes))!.GetDefaultValueSql());
        Assert.True(profile.FindProperty(nameof(UserProfile.FollowBlockUntil))!.IsNullable);
        Assert.Equal("smallint", profile.FindProperty(nameof(UserProfile.FollowBlockLevel))!.GetColumnType());
        Assert.True(m.FindEntityType(typeof(Follow))!.FindProperty(nameof(Follow.EffectsAt))!.IsNullable);

        Assert.Equal("gin", Index<UserProfile>(m, "ix_userprofiles_interests").GetMethod());
        // Places match by lower(), which no GIN index on the raw array can serve.
        Assert.DoesNotContain(m.FindEntityType(typeof(UserProfile))!.GetIndexes(), i => i.GetDatabaseName() == "ix_userprofiles_places");
        Assert.Equal("\"LastDistrictId\" IS NOT NULL", Index<UserProfile>(m, "ix_userprofiles_district").GetFilter());
        Assert.NotNull(Index<UserProfile>(m, "ix_userprofiles_state"));
        Assert.Equal("\"EffectsAt\" IS NULL", Index<Follow>(m, "ix_follows_effects_pending").GetFilter());
        Assert.Equal("\"IsPublic\"", Index<RentNearBy.Core.Entities.Trip>(m, "ix_trips_public_city").GetFilter());
        Assert.Equal("\"DueAt\" IS NOT NULL", Index<FollowPushGroup>(m, "ix_followpushgroups_due").GetFilter());
        Assert.NotNull(Index<DiscoverHide>(m, "ix_discoverhides_at"));
        Assert.Equal(2, m.FindEntityType(typeof(DiscoverHide))!.GetForeignKeys().Count(f => f.DeleteBehavior == DeleteBehavior.Cascade));
    }
}
