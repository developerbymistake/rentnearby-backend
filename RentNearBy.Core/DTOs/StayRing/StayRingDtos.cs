namespace RentNearBy.Core.DTOs.StayRing;

// GET/PUT /stays/me/enquiry-ring (contract F:/Dhasu/Buddy_resume/stay_ring/CONTRACT.md section 3).
public class StayRingSettingsDto
{
    // The owner's switch "Ring me for new enquiries" (default on).
    public bool RingOn { get; set; } = true;
    // The stay is approved and not deleted.
    public bool StayLive { get; set; }
    // At least one valid device token is registered.
    public bool HasValidToken { get; set; }
    // A new enquiry would ring now: switch on, stay live, a device token.
    public bool CanRing { get; set; }
    public int RingSeconds { get; set; }
}

public class UpdateStayRingRequest
{
    public bool? RingOn { get; set; }
}

// POST /stays/me/enquiry-ring/{enquiryId}/answer: the ring buttons. Never changes the enquiry.
public class StayRingAnswerRequest
{
    // "Accepted" (opened the enquiry) or "Declined" (ignored the ring).
    public string? Outcome { get; set; }
    // The answering device's FCM token: the ring stops on every OTHER device of the owner.
    public string? DeviceToken { get; set; }
}
