namespace RentNearBy.Core.Models;

public static class UserRoles
{
    public static readonly Guid SeekerId = new("10000000-0000-0000-0000-000000000001");
    public static readonly Guid RentOwnerId = new("10000000-0000-0000-0000-000000000002");
    public static readonly Guid PropertyOwnerId = new("10000000-0000-0000-0000-000000000003");
    public static readonly Guid StayOwnerId = new("10000000-0000-0000-0000-000000000004");
    public static readonly Guid TaxiDriverId = new("10000000-0000-0000-0000-000000000005");

    public const string Seeker = "SEEKER";
    public const string RentOwner = "RENT_OWNER";
    public const string PropertyOwner = "PROPERTY_OWNER";
    public const string StayOwner = "STAY_OWNER";
    public const string TaxiDriver = "TAXI_DRIVER";

    public const string ClaimType = "role";

    public static string KeyOf(Guid roleId) =>
        roleId == SeekerId ? Seeker
        : roleId == RentOwnerId ? RentOwner
        : roleId == PropertyOwnerId ? PropertyOwner
        : roleId == StayOwnerId ? StayOwner
        : roleId == TaxiDriverId ? TaxiDriver
        : throw new InvalidOperationException($"Unknown role id {roleId}");

    public static string? ListingKindOf(Guid roleId) =>
        roleId == RentOwnerId ? ListingKinds.Room
        : roleId == PropertyOwnerId ? ListingKinds.Plot
        : roleId == StayOwnerId ? ListingKinds.Stay
        : roleId == TaxiDriverId ? ListingKinds.Taxi
        : roleId == SeekerId ? null
        : throw new InvalidOperationException($"Unknown role id {roleId}");
}
