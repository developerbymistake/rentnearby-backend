using System.Globalization;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Owner;

namespace RentNearBy.Infrastructure.Services.Owner.Be4;

// Shared SQL pieces of the owner jobs (kind-agnostic). Listing tables are chosen from a fixed map, never from input.
public static class OwnerJobSql
{
    public static string Table(string kind) => kind switch
    {
        ListingKinds.Stay => "\"StayListings\"",
        ListingKinds.Taxi => "\"TaxiListings\"",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown listing kind"),
    };

    // The owner's active plan row (one at most), joined to the PlanKeySql CTE "pv".
    public static string PlanJoin(string listingAlias, string kind) => $"""
        {PlanRowSql.ActiveLateral(listingAlias + ".\"UserId\"", kind)}
        JOIN {PlanKeySql.DefaultName} ON {PlanKeySql.Join("up")}
        """;

    public const string ValidToken = """EXISTS (SELECT 1 FROM "DeviceTokens" dt WHERE dt."UserId" = {0}."UserId" AND dt."IsValid")""";

    public static string HasValidToken(string alias) => string.Format(CultureInfo.InvariantCulture, ValidToken, alias);

    // [start, end) of an IST day in UTC.
    public static (DateTime From, DateTime To) IstDayUtc(DateOnly day)
    {
        var from = DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue).AddHours(-5.5), DateTimeKind.Utc);
        return (from, from.AddDays(1));
    }

    public static NpgsqlParameter Uuid(string name, Guid value) => new(name, NpgsqlDbType.Uuid) { Value = value };

    public static NpgsqlParameter Date(string name, DateOnly value) => new(name, NpgsqlDbType.Date) { Value = value };

    public static NpgsqlParameter Int(string name, int value) => new(name, NpgsqlDbType.Integer) { Value = value };

    public static NpgsqlParameter Text(string name, string value) => new(name, NpgsqlDbType.Varchar) { Value = value };

    public static NpgsqlParameter NullableText(string name, string? value) => new(name, NpgsqlDbType.Varchar) { Value = (object?)value ?? DBNull.Value };

    public static NpgsqlParameter Utc(string name, DateTime value) => new(name, NpgsqlDbType.TimestampTz) { Value = DateTime.SpecifyKind(value, DateTimeKind.Utc) };

    public static TimeOnly? ParseIstTime(string? value)
        => TimeOnly.TryParseExact(value ?? string.Empty, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;
}
