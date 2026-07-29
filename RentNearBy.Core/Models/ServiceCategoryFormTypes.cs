namespace RentNearBy.Core.Models;

// Decides which of the two variable Enquiry fields (PreferredDateOrTripStart, NumberOfPeople) the
// Enquiry Form shows/labels for a category's Service+Package — see ServiceCategory.FormType.
public static class ServiceCategoryFormTypes
{
    public const string Travel = "Travel";
    public const string Event = "Event";
    public const string Consultation = "Consultation";
    public const string Education = "Education";
}
