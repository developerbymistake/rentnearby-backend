using RentNearBy.Core.Models.Owner;

namespace RentNearBy.Tests.Owner.Be1;

// Contract 2.2 and 3.2 (BlockedRanges): 1 to 90 nights, start today .. today + 180, overlap, bookings inside, the limit lines.
public class BlockedRangeLimitTests
{
    private static readonly DateOnly D = Be1Kit.Today;

    [Fact]
    [PlanKeyTest(PlanFeatureKeys.BlockedRanges)]
    public void Second_block_on_free_is_refused_plus_and_off_get_ten()
    {
        var free = Be1Kit.Free(ListingKinds.Stay);
        Assert.Equal(1, free.BlockedRanges);
        Assert.Equal("Free allows 1 blocked range.", OwnerErrorTypes.LimitMessage(OwnerErrorTypes.BlockedRangeLimit, free.BlockedRanges, free.CanUpgrade));
        var plus = Be1Kit.Plus(ListingKinds.Stay);
        Assert.Equal(10, plus.BlockedRanges);
        Assert.Equal("You can add up to 10 blocked ranges.", OwnerErrorTypes.LimitMessage(OwnerErrorTypes.BlockedRangeLimit, plus.BlockedRanges, plus.CanUpgrade));
        var off = Be1Kit.Off(ListingKinds.Stay);
        Assert.Equal(10, off.BlockedRanges);
        Assert.Equal("You can add up to 10 blocked ranges.", OwnerErrorTypes.LimitMessage(OwnerErrorTypes.BlockedRangeLimit, off.BlockedRanges, off.CanUpgrade));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(90, true)]
    [InlineData(91, false)]
    public void Block_is_1_to_90_nights(int nights, bool ok)
        => Assert.Equal(ok, BlockedRangeRules.Validate(D, D.AddDays(nights - 1), null, D) == null);

    [Fact]
    public void Zero_nights_is_invalid()
        => Assert.Equal(OwnerErrorTypes.BlockedRangeInvalid, BlockedRangeRules.Validate(D, D.AddDays(-1), null, D)!.Type);

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(180, true)]
    [InlineData(181, false)]
    public void Block_starts_today_to_180_days_ahead(int startIn, bool ok)
        => Assert.Equal(ok, BlockedRangeRules.Validate(D.AddDays(startIn), D.AddDays(startIn + 1), null, D) == null);

    [Fact]
    public void An_edit_may_keep_a_start_that_already_passed()
    {
        Assert.Null(BlockedRangeRules.Validate(D.AddDays(-3), D.AddDays(2), null, D, keptStart: D.AddDays(-3)));
        Assert.NotNull(BlockedRangeRules.Validate(D.AddDays(-2), D.AddDays(2), null, D, keptStart: D.AddDays(-3)));
        Assert.NotNull(BlockedRangeRules.Validate(D.AddDays(-5), D.AddDays(-1), null, D, keptStart: D.AddDays(-5)));
    }

    [Fact]
    public void Reason_is_at_most_60_characters()
    {
        Assert.Null(BlockedRangeRules.Validate(D, D, new string('r', 60), D));
        Assert.Equal(BlockedRangeRules.ReasonTooLong, BlockedRangeRules.Validate(D, D, new string('r', 61), D)!.Message);
    }

    [Fact]
    public void Overlap_is_any_shared_night()
    {
        Assert.True(BlockedRangeRules.Overlaps(D, D.AddDays(2), D.AddDays(2), D.AddDays(4)));
        Assert.False(BlockedRangeRules.Overlaps(D, D.AddDays(2), D.AddDays(3), D.AddDays(4)));
    }

    [Fact]
    public void A_booking_is_inside_when_one_of_its_nights_is_blocked()
    {
        var from = D.AddDays(10);
        var to = D.AddDays(12);
        Assert.True(BlockedRangeRules.BookingInside(D.AddDays(9), D.AddDays(11), from, to));
        Assert.True(BlockedRangeRules.BookingInside(D.AddDays(12), D.AddDays(14), from, to));
        Assert.False(BlockedRangeRules.BookingInside(D.AddDays(8), D.AddDays(10), from, to));   // checks out on the first blocked night
        Assert.False(BlockedRangeRules.BookingInside(D.AddDays(13), D.AddDays(15), from, to));
    }

    [Fact]
    public void Confirm_message_names_the_count()
    {
        Assert.Equal("1 confirmed booking is inside these days. It stays confirmed.", BlockedRangeRules.NeedsConfirmMessage(1));
        Assert.Equal("3 confirmed bookings are inside these days. They stay confirmed.", BlockedRangeRules.NeedsConfirmMessage(3));
    }
}
