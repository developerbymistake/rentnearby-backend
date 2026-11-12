using RentNearBy.Infrastructure.Services.Retention;

namespace RentNearBy.Tests.Retention;

public class DeclarativeRetentionRuleSqlTests
{
    private static DeclarativeRetentionRule Rule(string eligible, string order = "CreatedAt", string[]? keys = null)
        => new(new RetentionRuleDefinition("R", RetentionGroups.Notifications, "Things", "Id", eligible, order, 90, KeyColumns: keys));

    [Fact]
    public void Delete_sql_has_the_documented_shape()
    {
        var sql = Rule("t.\"CreatedAt\" < @cutoff").BuildDeleteSql();
        Assert.Equal(
            "DELETE FROM \"Things\" AS t WHERE t.\"Id\" IN (SELECT t.\"Id\" FROM \"Things\" AS t " +
            "WHERE (t.\"CreatedAt\" < @cutoff) ORDER BY t.\"CreatedAt\" LIMIT @n FOR UPDATE SKIP LOCKED)", sql);
    }

    [Fact]
    public void Top_level_or_is_wrapped_so_the_protection_is_not_bypassed()
    {
        var sql = Rule("A OR B").BuildDeleteSql();
        Assert.Contains("WHERE (A OR B) ORDER BY", sql);
        Assert.Contains("WHERE (A OR B) LIMIT", Rule("A OR B").BuildCountSql(10));
    }

    [Fact]
    public void Composite_key_uses_the_tuple_form()
    {
        var sql = Rule("x", keys: new[] { "UserId", "BannerId" }).BuildDeleteSql();
        Assert.Contains("WHERE (t.\"UserId\", t.\"BannerId\") IN (SELECT t.\"UserId\", t.\"BannerId\" FROM", sql);
    }

    [Fact]
    public void Order_expression_is_used_as_written()
        => Assert.Contains("ORDER BY GREATEST(t.\"A\", t.\"B\") LIMIT", Rule("x", "GREATEST(t.\"A\", t.\"B\")").BuildDeleteSql());

    [Fact]
    public void Count_sql_is_bounded_and_aliased_value()
    {
        var sql = Rule("x").BuildCountSql(200);
        Assert.Contains("AS \"Value\"", sql);
        Assert.Contains("LIMIT 201) x", sql);
    }

    [Fact]
    public void Rule_exposes_the_definition()
    {
        var r = Rule("x");
        Assert.Equal("R", r.Name);
        Assert.Equal(RetentionGroups.Notifications, r.Group);
        Assert.Equal(90, r.DefaultDays);
        Assert.True(r.DefaultEnabled);
        Assert.False(r.GuardExempt);
    }

    [Fact]
    public void Only_mentioned_parameters_are_sent()
    {
        var ctx = new RetentionContext(null!, new DateTime(2026, 11, 12, 1, 0, 0, DateTimeKind.Utc), new DateTime(2026, 8, 14, 1, 0, 0, DateTimeKind.Utc),
            new DateOnly(2026, 11, 12), new DateOnly(2026, 8, 14), 90);
        var p = RetentionSqlParams.For("... < @cutoff LIMIT @n", ctx, 500);
        Assert.Equal(new[] { "cutoff", "n" }, p.Select(x => x.ParameterName).ToArray());
        Assert.Equal(DateTimeKind.Utc, ((DateTime)p[0].Value!).Kind);
        var d = RetentionSqlParams.For("@cutoffDate @today @now", ctx, 0);
        Assert.Equal(new[] { "cutoffDate", "today", "now" }, d.Select(x => x.ParameterName).ToArray());
        Assert.IsType<DateOnly>(d[0].Value);
    }

    [Fact]
    public void Closed_enquiry_list_never_holds_open_or_failed_problem_states()
    {
        var closed = RetentionSql.ClosedEnquiry;
        foreach (var open in new[] { "Asked", "Offered", "Countered", "ConfirmedByBoth", "FareSent", "BookedByTraveller", "ProblemDriverNoShow", "ProblemHostNoRoom" })
            Assert.DoesNotContain("'" + open + "'", closed);
    }
}
