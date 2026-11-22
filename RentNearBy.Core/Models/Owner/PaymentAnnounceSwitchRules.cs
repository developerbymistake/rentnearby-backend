namespace RentNearBy.Core.Models.Owner;

// FB-41: an announce date only makes sense while payment is OFF. A new or changed date while payment is (or is being turned) ON
// is refused; turning payment ON clears a stored date, so the announce job never sends "Plans start on ..." afterwards.
public static class PaymentAnnounceSwitchRules
{
    public static bool RefuseAnnounce(DateOnly? askedDate, DateOnly? storedDate, bool enabledAfter)
        => askedDate is { } asked && asked != storedDate && enabledAfter;

    public static bool ClearsAnnounce(bool flippedOn) => flippedOn;
}
