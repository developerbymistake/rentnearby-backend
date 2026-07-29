namespace RentNearBy.Core.Models;

// Fixed, small set — deliberately NOT an admin-CRUD lookup table (see the plan's Design decisions).
// Matches EnquiryStatuses.cs's exact shape.
public static class EscalationReasons
{
    public const string NotResponding = "NotResponding";
    public const string Unhelpful = "Unhelpful";
    public const string WrongInformation = "WrongInformation";
    public const string Other = "Other";

    public static readonly string[] All = [NotResponding, Unhelpful, WrongInformation, Other];
}
