using RentNearBy.Core.Models;

namespace RentNearBy.Infrastructure.Services.Retention;

public sealed record RetentionSlot(string Group, TimeOnly StartIst, TimeOnly EndIst);

// Pure. Slots are back to back inside 01:00-04:30 IST, so two groups never overlap and the 05:00 purge and 08:00 reminders stay clear.
public static class RetentionSchedule
{
    public static IReadOnlyList<RetentionSlot> Slots { get; } = new[]
    {
        new RetentionSlot(RetentionGroups.Notifications, new TimeOnly(1, 0), new TimeOnly(1, 30)),
        new RetentionSlot(RetentionGroups.TripHousekeeping, new TimeOnly(1, 30), new TimeOnly(2, 0)),
        new RetentionSlot(RetentionGroups.Sessions, new TimeOnly(2, 0), new TimeOnly(2, 20)),
        new RetentionSlot(RetentionGroups.Devices, new TimeOnly(2, 20), new TimeOnly(2, 40)),
        new RetentionSlot(RetentionGroups.TripEnquiries, new TimeOnly(2, 40), new TimeOnly(3, 15)),
        new RetentionSlot(RetentionGroups.Chat, new TimeOnly(3, 15), new TimeOnly(4, 10)),
        new RetentionSlot(RetentionGroups.OwnerCalendar, new TimeOnly(4, 10), new TimeOnly(4, 30)),
    };

    // Run order across all groups; inside a group the order matters (due actions and taxi requests first, offers before enquiries, chat after enquiries).
// The six owner calendar rules (group D, lane BE1) run last, in their own slot.
    public static IReadOnlyList<string> RuleOrder { get; } = new[]
    {
        "NotificationEvents", "UserNotices", "NotificationLogs", "TripPushLogs",
        "OutboxMessages", "TripReminderRuns", "TripShortlistItems",
        "BuddyRequests", "TripMemberInvites", "TripPaymentsEnded", "DiscoverHides",
        // Trip compaction (UPDATE of old finished trips) runs after the quick housekeeping deletes above.
        "TripCompaction",
        "Sessions", "AdminSessions", "StayJobs", "BannerDismissals",
        "DeviceTokens", "AdminDeviceTokens",
        "DueActions", "TaxiRequests",
        "TripOffers", "TripEnquiries", "TripEnquiryOrphans",
        "Chat",
        "OwnerCalendarEntries", "StayBlockedRanges", "StaySpecialPrices", "FestivalReminderSent", "OwnerReminderRuns", "OwnerWeeklyStats", "TaxiDayOffs",
        "StayQuoteIssues",
    };

    public static int OrderOf(string ruleName)
    {
        for (var i = 0; i < RuleOrder.Count; i++)
            if (string.Equals(RuleOrder[i], ruleName, StringComparison.OrdinalIgnoreCase)) return i;
        return RuleOrder.Count;
    }

    public static DateTime SlotStartUtc(RetentionSlot slot, DateOnly istDate) => IstTime.AtUtc(istDate, slot.StartIst.Hour, slot.StartIst.Minute);

    public static DateTime SlotEndUtc(RetentionSlot slot, DateOnly istDate) => IstTime.AtUtc(istDate, slot.EndIst.Hour, slot.EndIst.Minute);

    public static RetentionSlot? DueSlot(DateTime nowUtc, IReadOnlySet<string> doneGroupsToday)
    {
        var date = IstTime.TodayIst(nowUtc);
        foreach (var slot in Slots)
        {
            if (doneGroupsToday.Contains(slot.Group)) continue;
            if (IstTime.InWindow(SlotStartUtc(slot, date), SlotEndUtc(slot, date), nowUtc)) return slot;
        }
        return null;
    }
}
