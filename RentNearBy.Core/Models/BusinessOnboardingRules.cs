namespace RentNearBy.Core.Models;

public enum OnboardingCheck
{
    Ok,
    DistrictNotFound,
    DistrictInactive,
    OutsideDistrict,
    OnboardingClosed,
}

public static class BusinessOnboardingRules
{
    public const string OnboardingClosedType = "OnboardingClosed";
    public const string OnboardingClosedMessage = "We can't take new listings in this area right now.";

    // numeric(9,6) storage can round a pin; anything within ~0.1 m is the same pin.
    public const decimal PinTolerance = 0.000001m;

    public static OnboardingCheck Decide(bool found, bool isActive, bool insideBoundary, bool onboardingEnabled)
    {
        if (!found) return OnboardingCheck.DistrictNotFound;
        if (!isActive) return OnboardingCheck.DistrictInactive;
        if (!insideBoundary) return OnboardingCheck.OutsideDistrict;
        if (!onboardingEnabled) return OnboardingCheck.OnboardingClosed;
        return OnboardingCheck.Ok;
    }

    public static bool PinMoved(decimal? oldLat, decimal? oldLng, decimal newLat, decimal newLng) =>
        oldLat == null || oldLng == null
        || Math.Abs(oldLat.Value - newLat) > PinTolerance
        || Math.Abs(oldLng.Value - newLng) > PinTolerance;

    // Save: a row that was ever approved skips the whole gate while it keeps its district and pin.
    public static bool NeedsFlagCheck(bool everApproved, Guid? oldDistrictId, Guid newDistrictId,
        decimal? oldLat, decimal? oldLng, decimal newLat, decimal newLng) =>
        !everApproved
        || oldDistrictId != newDistrictId
        || PinMoved(oldLat, oldLng, newLat, newLng);

    // Submit: district/pin were already checked at the save that set them, so only a never-approved row is gated here.
    public static bool NeedsFlagCheckAtSubmit(bool everApproved) => !everApproved;

    public static OnboardingCheck ForSave(OnboardingCheck check, bool everApproved, Guid? oldDistrictId, Guid newDistrictId,
        decimal? oldLat, decimal? oldLng, decimal newLat, decimal newLng) =>
        NeedsFlagCheck(everApproved, oldDistrictId, newDistrictId, oldLat, oldLng, newLat, newLng) ? check : OnboardingCheck.Ok;

    public static bool IsOutsideDistrict(OnboardingCheck check) =>
        check is OnboardingCheck.DistrictNotFound or OnboardingCheck.DistrictInactive or OnboardingCheck.OutsideDistrict;
}
