namespace RentNearBy.Tests.Trips;

public class HostReminderScheduleTests
{
    private static DateTime Ist(int m, int d, int h, int min = 0) =>
        IstTime.AtUtc(new DateOnly(2026, m, d), h, min);

    // Audit example: proof at 21:00 IST, hold until 21:00 IST next day. The old rule sent ~23 pushes incl. 00:0x..06:0x.
    [Fact]
    public void Night_slots_move_to_seven_and_merge_and_the_cap_keeps_the_last()
    {
        var slots = HostReminderSchedule.Slots(Ist(11, 11, 21), Ist(11, 12, 21));

        Assert.Equal(HostReminderSchedule.MaxReminders, slots.Count);
        Assert.Equal(Ist(11, 12, 7), slots[0]);
        Assert.Equal(Ist(11, 12, 8), slots[1]);
        Assert.Equal(Ist(11, 12, 17), slots[10]);
        Assert.Equal(Ist(11, 12, 20), slots[^1]);
        Assert.All(slots, s => Assert.False(IstTime.IsQuiet(s, 22, 7)));
        Assert.True(slots.Zip(slots.Skip(1)).All(p => p.Second - p.First >= TimeSpan.FromMinutes(60)));
    }

    [Fact]
    public void Slots_are_absolute_so_a_late_pass_does_not_drift_the_next_one()
    {
        var proof = Ist(11, 12, 10);
        var hold = Ist(11, 12, 16);
        Assert.Equal(new[] { Ist(11, 12, 11), Ist(11, 12, 12), Ist(11, 12, 13), Ist(11, 12, 14), Ist(11, 12, 15) },
            HostReminderSchedule.Slots(proof, hold));

        var due = HostReminderSchedule.Due(proof, hold, 1, Ist(11, 12, 11, 4));
        Assert.Equal((1, 2), due!.Value);
        Assert.Equal(Ist(11, 12, 12), HostReminderSchedule.NextDue(proof, hold, 2));
    }

    [Fact]
    public void Missed_slots_send_only_the_latest_one()
    {
        var proof = Ist(11, 11, 21);
        var hold = Ist(11, 12, 21);
        Assert.Null(HostReminderSchedule.Due(proof, hold, 1, Ist(11, 12, 6, 59)));
        Assert.Equal((3, 4), HostReminderSchedule.Due(proof, hold, 1, Ist(11, 12, 9, 30))!.Value);
        Assert.Equal(Ist(11, 12, 10), HostReminderSchedule.NextDue(proof, hold, 4));
        Assert.Null(HostReminderSchedule.Due(proof, hold, 4, Ist(11, 12, 9, 59)));
    }

    [Fact]
    public void A_hold_ending_at_two_am_gets_only_the_evening_slot()
    {
        var slots = HostReminderSchedule.Slots(Ist(11, 11, 20), Ist(11, 12, 2));
        Assert.Equal(new[] { Ist(11, 11, 21) }, slots);
        Assert.Null(HostReminderSchedule.NextDue(Ist(11, 11, 20), Ist(11, 12, 2), 2));
    }

    [Fact]
    public void A_short_hold_has_no_slot()
    {
        var proof = Ist(11, 12, 10);
        Assert.Empty(HostReminderSchedule.Slots(proof, proof.AddMinutes(60)));
        Assert.Null(HostReminderSchedule.NextDue(proof, proof.AddMinutes(60), 1));
        Assert.Null(HostReminderSchedule.Due(proof, proof.AddMinutes(60), 1, proof.AddMinutes(59)));
    }

    [Fact]
    public void Next_needs_proof_sent_and_a_running_hold()
    {
        var now = Ist(11, 12, 10, 30);
        var e = new TripEnquiry
        {
            Id = Guid.NewGuid(),
            Status = TripEnquiryStatuses.ConfirmedByBoth,
            AdvanceStatus = TripAdvanceStatuses.ProofSent,
            ProofSentAt = Ist(11, 12, 10),
            HoldUntil = Ist(11, 12, 16),
            HostReminderCount = 1,
        };
        Assert.Equal(Ist(11, 12, 11), HostReminderSchedule.Next(e, now));

        e.AdvanceStatus = TripAdvanceStatuses.Received;
        Assert.Null(HostReminderSchedule.Next(e, now));
        e.AdvanceStatus = TripAdvanceStatuses.ProofSent;
        e.HoldUntil = now;
        Assert.Null(HostReminderSchedule.Next(e, now));
    }

    [Fact]
    public void Collapse_key_is_stable_per_enquiry()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        Assert.Equal("advance-proof:11111111222233334444555555555555", HostReminderSchedule.CollapseKey(id));
    }
}
