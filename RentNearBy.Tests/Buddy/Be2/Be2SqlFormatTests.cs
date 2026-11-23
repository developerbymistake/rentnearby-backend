using System.Globalization;
using System.Reflection;
using RentNearBy.Infrastructure.Services.Buddy.Be2;

namespace RentNearBy.Tests.Buddy.Be2;

// SqlQueryRaw / ExecuteSqlRaw run string.Format on the SQL text. A literal brace pair left undoubled throws FormatException
// at run time (this broke trip invite accept and decline). No database needed.
public class Be2SqlFormatTests
{
    private static IEnumerable<(string Name, string Sql)> AllSql() =>
        typeof(Be2Sql).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string) && f.Name.EndsWith("Sql", StringComparison.Ordinal))
            .Select(f => (f.Name, (string)f.GetRawConstantValue()!));

    [Fact]
    public void Every_Be2_sql_text_survives_string_format()
    {
        var args = Enumerable.Range(0, 12).Select(i => (object)("@p" + i)).ToArray();
        foreach (var (name, sql) in AllSql())
        {
            var ex = Record.Exception(() => string.Format(CultureInfo.InvariantCulture, sql, args));
            Assert.True(ex is null, name + ": " + ex?.Message);
        }
    }

    [Fact]
    public void Invite_lean_sql_keeps_the_empty_json_literal()
    {
        var formatted = string.Format(CultureInfo.InvariantCulture, Be2Sql.InviteLeanSql, "@member");
        Assert.Contains("'{}'::text AS \"PlanJson\"", formatted);
    }
}
