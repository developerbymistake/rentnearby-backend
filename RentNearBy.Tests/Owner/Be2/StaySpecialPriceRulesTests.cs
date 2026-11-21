using RentNearBy.Core.DTOs.Owner;
using RentNearBy.Core.Models.Owner;

namespace RentNearBy.Tests.Owner.Be2;

// Contract 2.3 / 4.3 / 11.1 (lane BE2): percent by direction and step 5, 1..60 nights, start today..today + 180, overlap,
// 3 saves a day rolling at 00:00 IST.
public class StaySpecialPriceRulesTests
{
    private static readonly DateOnly Today = new(2026, 11, 21);
    private const string Lower = SpecialPriceDirections.Lower;
    private const string Higher = SpecialPriceDirections.Higher;

    [Theory]
    [InlineData(Lower, 5, true)]
    [InlineData(Lower, 50, true)]
    [InlineData(Lower, 55, false)]
    [InlineData(Lower, 0, false)]
    [InlineData(Lower, 12, false)]
    [InlineData(Higher, 5, true)]
    [InlineData(Higher, 100, true)]
    [InlineData(Higher, 105, false)]
    [InlineData(Higher, 3, false)]
    public void Percent_by_direction_in_steps_of_five(string direction, int percent, bool ok)
        => Assert.Equal(ok, StaySpecialPriceRules.PercentOk(direction, percent));

    [Fact]
    public void Percent_lines_follow_the_direction()
    {
        Assert.Equal(OwnerErrorTypes.SpecialPricePercentLower, StaySpecialPriceRules.Invalid(Lower, 60, Today, Today, Today));
        Assert.Equal(OwnerErrorTypes.SpecialPricePercentHigher, StaySpecialPriceRules.Invalid(Higher, 110, Today, Today, Today));
        Assert.Equal("Pick a percent between 5 and 50.", OwnerErrorTypes.SpecialPricePercentLower);
        Assert.Equal("Pick a percent between 5 and 100.", OwnerErrorTypes.SpecialPricePercentHigher);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(60, true)]
    [InlineData(61, false)]
    public void Nights_one_to_sixty(int nights, bool ok)
    {
        var to = Today.AddDays(nights - 1);
        Assert.Equal(nights, StaySpecialPriceRules.Nights(Today, to));
        Assert.Equal(ok ? null : OwnerErrorTypes.SpecialPriceTooLong, StaySpecialPriceRules.Invalid(Lower, 15, Today, to, Today));
    }

    [Fact]
    public void End_before_start_is_refused()
        => Assert.Equal(OwnerErrorTypes.SpecialPriceTooLong, StaySpecialPriceRules.Invalid(Lower, 15, Today.AddDays(2), Today.AddDays(1), Today));

    [Theory]
    [InlineData(0, true)]
    [InlineData(180, true)]
    [InlineData(181, false)]
    [InlineData(-1, false)]
    public void Start_today_to_today_plus_180(int startIn, bool ok)
    {
        var from = Today.AddDays(startIn);
        Assert.Equal(ok ? null : OwnerErrorTypes.SpecialPriceStartTooLate, StaySpecialPriceRules.Invalid(Higher, 20, from, from.AddDays(2), Today));
    }

    [Fact]
    public void An_ongoing_rule_keeps_its_past_start_on_edit_but_a_new_past_start_is_refused()
    {
        var started = Today.AddDays(-2);
        Assert.Null(StaySpecialPriceRules.Invalid(Lower, 20, started, Today.AddDays(3), Today, keepFrom: started));
        Assert.Equal(OwnerErrorTypes.SpecialPriceStartTooLate, StaySpecialPriceRules.Invalid(Lower, 20, Today.AddDays(-1), Today.AddDays(3), Today, keepFrom: started));
        Assert.Equal(OwnerErrorTypes.SpecialPriceStartTooLate, StaySpecialPriceRules.Invalid(Lower, 20, started, Today.AddDays(-1), Today, keepFrom: started));
    }

    [Theory]
    [InlineData(10, 12, 12, 14, true)]
    [InlineData(10, 12, 13, 14, false)]
    [InlineData(10, 20, 12, 14, true)]
    [InlineData(13, 14, 10, 12, false)]
    public void Overlap_is_any_shared_night(int aFrom, int aTo, int bFrom, int bTo, bool overlaps)
        => Assert.Equal(overlaps, StaySpecialPriceRules.Overlaps(D(aFrom), D(aTo), D(bFrom), D(bTo)));

    [Fact]
    public void Three_saves_a_day_then_refused_and_the_counter_rolls_at_midnight_ist()
    {
        Assert.False(StaySpecialPriceRules.SavesLimitReached(Today, 2, Today));
        Assert.Equal(1, StaySpecialPriceRules.SavesLeft(Today, 2, Today));
        Assert.True(StaySpecialPriceRules.SavesLimitReached(Today, 3, Today));
        Assert.Equal(0, StaySpecialPriceRules.SavesLeft(Today, 3, Today));
        // Yesterday's 3 saves read 0 today.
        Assert.False(StaySpecialPriceRules.SavesLimitReached(Today.AddDays(-1), 3, Today));
        Assert.Equal(3, StaySpecialPriceRules.SavesLeft(Today.AddDays(-1), 3, Today));
        Assert.Equal(0, StaySpecialPriceRules.SavesUsed(null, 0, Today));
        // 23:59 IST on the 21st and 00:01 IST on the 22nd are two IST days.
        var before = Core.Models.IstTime.TodayIst(new DateTime(2026, 11, 21, 18, 29, 0, DateTimeKind.Utc));
        var after = Core.Models.IstTime.TodayIst(new DateTime(2026, 11, 21, 18, 31, 0, DateTimeKind.Utc));
        Assert.Equal(Today, before);
        Assert.Equal(Today.AddDays(1), after);
        Assert.False(StaySpecialPriceRules.SavesLimitReached(before, 3, after));
    }

    [Fact]
    public void Same_rule_is_a_replay_any_change_is_not()
    {
        Assert.True(StaySpecialPriceRules.SameRule(D(6), D(12), Higher, 20, D(6), D(12), Higher, 20));
        Assert.False(StaySpecialPriceRules.SameRule(D(6), D(12), Higher, 25, D(6), D(12), Higher, 20));
        Assert.False(StaySpecialPriceRules.SameRule(D(6), D(13), Higher, 20, D(6), D(12), Higher, 20));
        Assert.False(StaySpecialPriceRules.SameRule(D(6), D(12), Lower, 20, D(6), D(12), Higher, 20));
    }

    [Fact]
    public void Label_is_the_festival_name_trimmed_to_forty()
    {
        Assert.Null(StaySpecialPriceRules.Label(null));
        Assert.Null(StaySpecialPriceRules.Label("  "));
        Assert.Equal("Diwali", StaySpecialPriceRules.Label(" Diwali "));
        Assert.Equal(40, StaySpecialPriceRules.Label(new string('x', 55))!.Length);
    }

    [Fact]
    public void Touching_festivals_are_those_sharing_a_night()
    {
        var diwali = new FestivalDto(Guid.NewGuid(), "Diwali", D(6), D(12));
        var holi = new FestivalDto(Guid.NewGuid(), "Holi", D(20), D(21));
        Assert.Equal([diwali.Id], StaySpecialPriceRules.TouchingFestivals(D(12), D(14), [diwali, holi]));
        Assert.Empty(StaySpecialPriceRules.TouchingFestivals(D(13), D(19), [diwali, holi]));
        Assert.Equal(2, StaySpecialPriceRules.TouchingFestivals(D(1), D(25), [diwali, holi]).Count);
    }

    [Fact]
    public void Only_active_rules_and_blocks_reach_the_function()
    {
        var rows = new List<StayRuleRow>
        {
            new() { From = D(1), To = Today.AddDays(-1), Lower = true, Percent = 15 },
            new() { From = Today, To = Today.AddDays(3), Lower = false, Percent = 20 },
        };
        var rules = StaySpecialPriceRules.ActiveRules(rows, Today);
        Assert.Equal([new PriceRule(Today, Today.AddDays(3), false, 20)], rules);
        Assert.Empty(StaySpecialPriceRules.ActiveRules(null, Today));
        var blocks = StaySpecialPriceRules.ActiveBlocks([new StayBlockRow { From = D(1), To = Today.AddDays(-1) }, new StayBlockRow { From = Today, To = Today }], Today);
        Assert.Equal([(Today, Today)], blocks);
    }

    [Fact]
    public void Limit_lines_follow_can_upgrade()
    {
        Assert.Equal("1 special price on Free.", OwnerErrorTypes.LimitMessage(OwnerErrorTypes.SpecialPriceLimit, 1, canUpgrade: true));
        Assert.Equal("You can add up to 5 special prices.", OwnerErrorTypes.LimitMessage(OwnerErrorTypes.SpecialPriceLimit, 5, canUpgrade: false));
        Assert.Equal(409, OwnerErrorTypes.StatusOf(OwnerErrorTypes.SpecialPriceLimit));
        Assert.Equal(409, OwnerErrorTypes.StatusOf(OwnerErrorTypes.SpecialPriceOverlap));
        Assert.Equal(409, OwnerErrorTypes.StatusOf(OwnerErrorTypes.SpecialPriceSavesLimit));
        Assert.Equal(400, OwnerErrorTypes.StatusOf(OwnerErrorTypes.SpecialPriceInvalid));
    }

    private static DateOnly D(int day) => new(2026, 11, day);
}
