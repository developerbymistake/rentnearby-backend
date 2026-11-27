namespace RentNearBy.Core.Models.Owner;

// The review fields of a stay (contract 3.5, AU-5) as one value: the live columns, or what the owner asked for.
public sealed record StayReviewValues(
    string? Name,
    Guid RegistrationTypeId,
    string RegistrationTypeName,
    string? RegistrationOtherName,
    string RegistrationNumber,
    string? Gstin,
    decimal? Latitude,
    decimal? Longitude,
    string? Address,
    Guid? DistrictId,
    string? DistrictName,
    Guid? CityId,
    string? CityName);

// PendingEditJson: the changed field names and the full asked values (so a field changed to null is kept apart from "unchanged").
// Note: the owner's optional reason (quotation name-change sheet); a later save from the edit screen replaces the change and drops it.
public sealed record StayPendingEdit(IReadOnlyList<string> Fields, StayReviewValues Asked, string? Note = null);

public static class StayEditResults
{
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
}

public static class StayReviewFields
{
    public const string Name = "name";
    public const string RegistrationType = "registrationType";
    public const string RegistrationOtherName = "registrationOtherName";
    public const string RegistrationNumber = "registrationNumber";
    public const string Gstin = "gstin";
    public const string Location = "location";
    public const string Address = "address";
    public const string District = "district";
    public const string City = "city";

    public static readonly IReadOnlyList<string> All =
        [Name, RegistrationType, RegistrationOtherName, RegistrationNumber, Gstin, Location, Address, District, City];

    // Field names whose asked value differs from the live value, in the order of All. Empty = nothing for the admin.
    public static IReadOnlyList<string> Diff(StayReviewValues live, StayReviewValues asked)
    {
        var fields = new List<string>();
        if (!string.Equals(live.Name, asked.Name, StringComparison.Ordinal)) fields.Add(Name);
        if (live.RegistrationTypeId != asked.RegistrationTypeId) fields.Add(RegistrationType);
        if (!string.Equals(live.RegistrationOtherName, asked.RegistrationOtherName, StringComparison.Ordinal)) fields.Add(RegistrationOtherName);
        if (!string.Equals(live.RegistrationNumber, asked.RegistrationNumber, StringComparison.Ordinal)) fields.Add(RegistrationNumber);
        if (!string.Equals(live.Gstin, asked.Gstin, StringComparison.Ordinal)) fields.Add(Gstin);
        if (live.Latitude != asked.Latitude || live.Longitude != asked.Longitude) fields.Add(Location);
        if (!string.Equals(live.Address, asked.Address, StringComparison.Ordinal)) fields.Add(Address);
        if (live.DistrictId != asked.DistrictId) fields.Add(District);
        if (live.CityId != asked.CityId) fields.Add(City);
        return fields;
    }

    // What the save does with the review fields of an Approved stay: nothing pending (equal to live), the same pending change
    // again (no write), or a new pending change.
    public enum SaveOutcome { ClearPending, Unchanged, NewPending }

    public static SaveOutcome Decide(StayReviewValues live, StayReviewValues asked, StayPendingEdit? current)
    {
        var fields = Diff(live, asked);
        if (fields.Count == 0) return SaveOutcome.ClearPending;
        if (current != null && current.Fields.SequenceEqual(fields) && Diff(current.Asked, asked).Count == 0) return SaveOutcome.Unchanged;
        return SaveOutcome.NewPending;
    }

    // The result strip of o_st_64 shows for 7 days after the decision and only while no newer change is waiting.
    public static bool StripVisible(string? result, DateTime? resultAt, bool hasPending, DateTime nowUtc)
        => result is StayEditResults.Approved or StayEditResults.Rejected
           && resultAt is { } at
           && !hasPending
           && at >= nowUtc.AddDays(-OwnerLimits.EditResultStripDays);
}
