namespace RentNearBy.Tests.Trips;

public class HostReminderTallyTests
{
    private static readonly Guid Id = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly DateTime ProofAt = new(2026, 11, 1, 10, 30, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(TripSendOutcome.Sent, true)]
    [InlineData(TripSendOutcome.Duplicate, true)]
    [InlineData(TripSendOutcome.Failed, false)]
    public void Cursor_MovesOnlyWhenTheKeyIsLogged(TripSendOutcome outcome, bool advances)
        => Assert.Equal(advances, TripSendOutcomes.AdvancesCursor(outcome));

    [Fact]
    public void FailedLastSlot_IsRetriedNextTick()
    {
        var proof = new DateTime(2026, 11, 2, 2, 0, 0, DateTimeKind.Utc);
        var hold = new DateTime(2026, 11, 2, 12, 30, 0, DateTimeKind.Utc);
        var slots = HostReminderSchedule.Slots(proof, hold);
        var lastNumber = slots.Count;
        var count = lastNumber;
        var at = slots[^1].AddMinutes(1);

        var due = HostReminderSchedule.Due(proof, hold, count, at);
        Assert.NotNull(due);
        Assert.Equal(lastNumber, due!.Value.Number);

        if (TripSendOutcomes.AdvancesCursor(TripSendOutcome.Failed)) count = due.Value.NextCount;
        var retry = HostReminderSchedule.Due(proof, hold, count, at.AddMinutes(5));
        Assert.Equal(lastNumber, retry?.Number);
    }

    [Fact]
    public void KeyPrefix_MatchesProofKey()
    {
        var prefix = HostReminderTally.KeyPrefix(Id, ProofAt);

        Assert.Equal("proof:11111111-2222-3333-4444-555555555555:202611011030:", prefix);
        Assert.StartsWith(prefix, TripAdvanceRules.ProofKey(Id, ProofAt, 7));
    }

    [Fact]
    public void Sent_RightAfterProof_IsZero()
        => Assert.Equal(0, HostReminderTally.Sent([TripAdvanceRules.ProofKey(Id, ProofAt, 0)], Id, ProofAt));

    [Fact]
    public void Sent_AfterOutage_CountsOnlySentSlots()
    {
        var keys = new[]
        {
            TripAdvanceRules.ProofKey(Id, ProofAt, 0),
            TripAdvanceRules.ProofKey(Id, ProofAt, 4),
        };

        Assert.Equal(1, HostReminderTally.Sent(keys, Id, ProofAt));
    }

    [Fact]
    public void Sent_IgnoresOtherProofsAndOtherKinds()
    {
        var older = ProofAt.AddHours(-30);
        var keys = new[]
        {
            TripAdvanceRules.ProofKey(Id, older, 1),
            TripAdvanceRules.ProofKey(Id, older, 2),
            TripAdvanceRules.ProofKey(Guid.NewGuid(), ProofAt, 1),
            TripAdvanceRules.HoldKey(Id, "t"),
            TripAdvanceRules.ProofKey(Id, ProofAt, 1),
            TripAdvanceRules.ProofKey(Id, ProofAt, 2),
            TripAdvanceRules.ProofKey(Id, ProofAt, 2),
        };

        Assert.Equal(2, HostReminderTally.Sent(keys, Id, ProofAt));
    }

    [Fact]
    public void Sent_NoProofOrNoKeys_IsZero()
    {
        Assert.Equal(0, HostReminderTally.Sent([TripAdvanceRules.ProofKey(Id, ProofAt, 3)], Id, null));
        Assert.Equal(0, HostReminderTally.Sent([], Id, ProofAt));
    }
}
