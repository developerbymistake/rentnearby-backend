namespace RentNearBy.Core.Models.Owner;

internal static class OwnerSql
{
    public static string List(IEnumerable<string> values) => string.Join(", ", values.Select(v => $"'{v}'"));
}

public static class OwnerListingKinds
{
    public const string Stay = ListingKinds.Stay;
    public const string Taxi = ListingKinds.Taxi;

    public static readonly IReadOnlyList<string> All = [Stay, Taxi];
    public static readonly string SqlList = OwnerSql.List(All);

    public static bool IsValid(string? kind) => kind is Stay or Taxi;
}

public static class CalendarEntryKinds
{
    public const string Auto = "Auto";
    public const string Manual = "Manual";

    public static readonly IReadOnlyList<string> All = [Auto, Manual];
    public static readonly string SqlList = OwnerSql.List(All);
}

public static class CalendarSourceTypes
{
    public const string StayEnquiry = "StayEnquiry";
    public const string TaxiBooking = "TaxiBooking";
    public const string TaxiLead = "TaxiLead";

    public static readonly IReadOnlyList<string> All = [StayEnquiry, TaxiBooking, TaxiLead];
    public static readonly string SqlList = OwnerSql.List(All);
}

public static class CalendarCancelledBy
{
    public const string Traveller = "Traveller";
    public const string Owner = "Owner";
    public const string Driver = "Driver";
    public const string Admin = "Admin";
    public const string System = "System";

    public static readonly IReadOnlyList<string> All = [Traveller, Owner, Driver, Admin, System];
    public static readonly string SqlList = OwnerSql.List(All);
}

public static class SpecialPriceDirections
{
    public const string Lower = "Lower";
    public const string Higher = "Higher";

    public static readonly IReadOnlyList<string> All = [Lower, Higher];
    public static readonly string SqlList = OwnerSql.List(All);
}

public static class PendingEditResults
{
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";

    public static readonly IReadOnlyList<string> All = [Approved, Rejected];
    public static readonly string SqlList = OwnerSql.List(All);
}

// Wire values of CalendarEntryDto.chip (MoneyNoteMath).
public static class MoneyChips
{
    public const string None = "None";
    public const string ToCollect = "ToCollect";
    public const string PaidInFull = "PaidInFull";
    public const string Received = "Received";
}
