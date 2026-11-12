using RentNearBy.Infrastructure.Services.Retention;

namespace RentNearBy.Tests.Retention;

public class RetentionScheduleTests
{
    private static readonly DateOnly Day = new(2026, 11, 12);
    private static readonly IReadOnlySet<string> None = new HashSet<string>();

    [Fact]
    public void Slots_are_six_back_to_back_and_never_overlap()
    {
        var slots = RetentionSchedule.Slots;
        Assert.Equal(6, slots.Count);
        for (var i = 0; i < slots.Count; i++)
        {
            Assert.True(slots[i].StartIst < slots[i].EndIst);
            if (i > 0) Assert.True(slots[i - 1].EndIst <= slots[i].StartIst);
        }
        Assert.Equal(slots.Count, slots.Select(s => s.Group).Distinct().Count());
    }

    [Fact]
    public void Slots_sit_inside_the_night_window_and_clear_of_the_reminder_run()
    {
        Assert.Equal(new TimeOnly(1, 0), RetentionSchedule.Slots[0].StartIst);
        Assert.Equal(new TimeOnly(4, 30), RetentionSchedule.Slots[^1].EndIst);
        // Reminders start 08:00 IST, the account purge 05:00 IST.
        Assert.All(RetentionSchedule.Slots, s => Assert.True(s.EndIst <= new TimeOnly(5, 0)));
    }

    [Fact]
    public void Every_slot_group_is_known_and_every_group_has_a_slot()
    {
        Assert.Equal(RetentionGroups.All.OrderBy(x => x), RetentionSchedule.Slots.Select(s => s.Group).OrderBy(x => x));
    }

    [Theory]
    [InlineData(1, 0, "A.Notifications")]
    [InlineData(1, 29, "A.Notifications")]
    [InlineData(1, 30, "B.Housekeeping")]
    [InlineData(2, 0, "C.Sessions")]
    [InlineData(2, 20, "A.Devices")]
    [InlineData(2, 40, "B.Enquiries")]
    [InlineData(3, 15, "A.Chat")]
    [InlineData(4, 29, "A.Chat")]
    public void DueSlot_picks_the_slot_that_holds_the_time(int h, int m, string group)
    {
        var now = IstTime.AtUtc(Day, h, m);
        Assert.Equal(group, RetentionSchedule.DueSlot(now, None)?.Group);
    }

    [Theory]
    [InlineData(0, 59)]
    [InlineData(4, 30)]
    [InlineData(8, 0)]
    [InlineData(12, 0)]
    public void DueSlot_is_null_outside_the_window(int h, int m)
        => Assert.Null(RetentionSchedule.DueSlot(IstTime.AtUtc(Day, h, m), None));

    [Fact]
    public void DueSlot_skips_a_group_done_today()
    {
        var done = new HashSet<string> { RetentionGroups.Notifications };
        Assert.Null(RetentionSchedule.DueSlot(IstTime.AtUtc(Day, 1, 10), done));
    }

    [Fact]
    public void SlotEndUtc_is_the_next_slot_start_or_window_end()
    {
        var slots = RetentionSchedule.Slots;
        for (var i = 0; i < slots.Count; i++)
        {
            var end = RetentionSchedule.SlotEndUtc(slots[i], Day);
            var expected = i + 1 < slots.Count ? RetentionSchedule.SlotStartUtc(slots[i + 1], Day) : IstTime.AtUtc(Day, 4, 30);
            Assert.Equal(expected, end);
        }
    }

    [Fact]
    public void Rule_order_puts_offers_before_enquiries_and_chat_last()
    {
        var o = RetentionSchedule.RuleOrder;
        Assert.True(o.ToList().IndexOf("TripOffers") < o.ToList().IndexOf("TripEnquiries"));
        Assert.Equal("Chat", o[^1]);
        Assert.Equal(16, o.Distinct().Count());
        Assert.Equal(o.Count, RetentionSchedule.OrderOf("Nope"));
    }
}
