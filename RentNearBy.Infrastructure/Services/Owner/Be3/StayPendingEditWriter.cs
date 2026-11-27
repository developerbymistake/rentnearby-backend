using RentNearBy.Core.Entities;
using RentNearBy.Core.Models.Owner;

namespace RentNearBy.Infrastructure.Services.Owner.Be3;

// The one place that turns asked review values of an Approved stay into the pending change (contract 3.5): used by the edit
// screen save (PUT /stays/me/basic) and by the quotation name-change request. The caller saves and clears the reject notice.
public static class StayPendingEditWriter
{
    public static StayReviewValues Live(StayListing stay) => new(stay.Name, stay.RegistrationTypeId, stay.RegistrationTypeName,
        stay.RegistrationOtherName, stay.RegistrationNumber, stay.Gstin, stay.Latitude, stay.Longitude, stay.Address, stay.DistrictId,
        stay.District?.Name, stay.CityId, stay.City?.Name);

    // What the admin should see next: the waiting change's asked values when one exists, else the live values.
    public static StayReviewValues Basis(StayListing stay) => StayPendingEditJson.Read(stay.PendingEditJson)?.Asked ?? Live(stay);

    public static StayReviewFields.SaveOutcome Apply(StayListing stay, StayReviewValues asked, DateTime now, string? note = null)
    {
        var live = Live(stay);
        var current = StayPendingEditJson.Read(stay.PendingEditJson);
        var outcome = StayReviewFields.Decide(live, asked, current);
        if (outcome == StayReviewFields.SaveOutcome.Unchanged && note != null && current!.Note != note)
            outcome = StayReviewFields.SaveOutcome.NewPending;
        if (outcome == StayReviewFields.SaveOutcome.ClearPending)
        {
            stay.PendingEditJson = null;
            stay.PendingEditAt = null;
        }
        else if (outcome == StayReviewFields.SaveOutcome.NewPending)
        {
            stay.PendingEditJson = StayPendingEditJson.Write(new StayPendingEdit(StayReviewFields.Diff(live, asked), asked, note));
            stay.PendingEditAt = now;
            stay.PendingEditResult = null;
            stay.PendingEditRejectReason = null;
            stay.PendingEditResultAt = null;
        }
        return outcome;
    }
}
