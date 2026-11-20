using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.DTOs.Buddy;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Models.TaxiRequests;

namespace RentNearBy.Infrastructure.Services.Buddy.Be3;

// SQL fragments, parameters and row shapes shared by the expense lane's services.
internal static class Be3Sql
{
    public const string SharesJsonOf = """
        (SELECT json_agg(json_build_object('userId', s."UserId", 'share', s."Share"))::text FROM "TripExpenseShares" s WHERE s."ExpenseId" = e."Id")
        """;

    public const string EditedTimesOf = """
        (SELECT count(*)::int FROM "TripExpenseRevisions" r WHERE r."ExpenseId" = e."Id" AND r."Kind" IN ('Edited','SharedWith'))
        """;

    // Label source and status of a link: Stay = enquiry name + status, Taxi = "driver|vehicle" + booking status. null = not in the trip.
    public static string LinkColumns(string kind, string id, string trip, string prefix) => $"""
        CASE WHEN {kind} = 'Stay' THEN (SELECT q."ListingName" FROM "TripEnquiries" q WHERE q."Id" = {id} AND q."TripId" = {trip} AND q."Kind" = 'Stay')
             WHEN {kind} = 'Taxi' THEN (SELECT b."DriverName" || '|' || b."VehicleName" FROM "TaxiBookings" b WHERE b."Id" = {id} AND b."TripId" = {trip}) END AS "{prefix}Label",
        CASE WHEN {kind} = 'Stay' THEN (SELECT q."Status" FROM "TripEnquiries" q WHERE q."Id" = {id} AND q."TripId" = {trip} AND q."Kind" = 'Stay')
             WHEN {kind} = 'Taxi' THEN (SELECT b."Status" FROM "TaxiBookings" b WHERE b."Id" = {id} AND b."TripId" = {trip}) END AS "{prefix}Status"
        """;

    public static bool StayCancelled(string status)
        => !TripEnquiryStatuses.Open.Contains(status) && status != TripEnquiryStatuses.Closed;

    public static bool LinkCancelled(string? kind, string? status)
        => status is not null && (kind == ExpenseLinkKinds.Taxi ? status == BookingStates.Cancelled : StayCancelled(status));

    public static ExpenseLinkDto? Link(string? kind, Guid? id, string? labelSource, string? status)
    {
        if (kind is null || id is null) return null;
        string label;
        if (labelSource is null) label = string.Empty;
        else if (kind == ExpenseLinkKinds.Taxi)
        {
            var parts = labelSource.Split('|', 2);
            label = TripExpenseRules.TaxiLabel(parts[0], parts.Length > 1 ? parts[1] : string.Empty);
        }
        else label = labelSource;
        return new ExpenseLinkDto { Kind = kind, Id = id.Value, Label = label, Cancelled = LinkCancelled(kind, status) };
    }

    public static NpgsqlParameter Uuid(string name, Guid value) => new(name, NpgsqlDbType.Uuid) { Value = value };

    public static NpgsqlParameter Uuid(string name, Guid? value) => new(name, NpgsqlDbType.Uuid) { Value = (object?)value ?? DBNull.Value };

    public static NpgsqlParameter Int(string name, int value) => new(name, NpgsqlDbType.Integer) { Value = value };

    public static NpgsqlParameter Small(string name, int value) => new(name, NpgsqlDbType.Smallint) { Value = (short)value };

    public static NpgsqlParameter Text(string name, string? value) => new(name, NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value };

    public static NpgsqlParameter Bool(string name, bool value) => new(name, NpgsqlDbType.Boolean) { Value = value };

    public static NpgsqlParameter Time(string name, DateTime value) => new(name, NpgsqlDbType.TimestampTz) { Value = DateTime.SpecifyKind(value, DateTimeKind.Utc) };

    public static NpgsqlParameter Uuids(string name, IEnumerable<Guid> values) => new(name, NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = values.ToArray() };

    public static NpgsqlParameter NullableUuids(string name, IEnumerable<Guid?> values)
        => new(name, NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = values.ToArray() };

    public static NpgsqlParameter Ints(string name, IEnumerable<int> values) => new(name, NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = values.ToArray() };

    public static NpgsqlParameter Texts(string name, IEnumerable<string> values) => new(name, NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = values.ToArray() };

    public static ExpenseDetailDto Detail(
        Guid tripId, Guid id, ExpenseFacts f, int version, int editedTimes, DateTime createdAt, DateTime updatedAt, bool deleted,
        ExpenseLinkDto? link, DateOnly? startDate, IReadOnlyList<Guid> canonical) => new()
    {
        Id = id,
        TripId = tripId,
        Title = f.Title,
        Category = f.Category,
        Amount = f.Amount,
        DayNumber = f.DayNumber,
        Date = TripExpenseRules.DateOf(startDate, f.DayNumber),
        Link = link,
        PaidByUserId = f.PaidByUserId,
        SplitMode = f.SplitMode,
        LeftoverUserId = f.LeftoverUserId,
        Note = f.Note,
        Version = version,
        EditedTimes = editedTimes,
        CreatedAt = createdAt,
        UpdatedAt = updatedAt,
        Deleted = deleted,
        Shares = TripExpenseRules.ShareDtos(f.Shares, canonical),
    };

    public static string Role(TripAccessLevel level) => level.ToString();
}
