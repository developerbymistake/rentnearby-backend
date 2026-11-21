using RentNearBy.Api.Validators.Owner;
using RentNearBy.Core.DTOs.Owner;
using RentNearBy.Core.Models.Owner;

namespace RentNearBy.Tests.Owner.Be2;

// Festival (A2) and special price request shapes.
public class Be2ValidatorTests
{
    private static readonly DateOnly Nov6 = new(2026, 11, 6);
    private readonly AdminFestivalRequestValidator _festival = new();

    private static AdminFestivalRequest Festival(string name = "Diwali", int days = 7, string? state = null)
        => new() { Id = Guid.NewGuid(), Name = name, DateFrom = Nov6, DateTo = Nov6.AddDays(days - 1), StateName = state };

    [Fact]
    public void Festival_name_one_to_forty_characters()
    {
        Assert.True(_festival.Validate(Festival(new string('a', 40))).IsValid);
        Assert.False(_festival.Validate(Festival(new string('a', 41))).IsValid);
        Assert.False(_festival.Validate(Festival("   ")).IsValid);
        Assert.False(_festival.Validate(Festival("")).IsValid);
    }

    [Fact]
    public void Festival_at_most_thirty_days_and_end_not_before_start()
    {
        Assert.True(_festival.Validate(Festival(days: 1)).IsValid);
        Assert.True(_festival.Validate(Festival(days: 30)).IsValid);
        Assert.False(_festival.Validate(Festival(days: 31)).IsValid);
        var backwards = Festival();
        backwards.DateTo = Nov6.AddDays(-1);
        Assert.False(_festival.Validate(backwards).IsValid);
        Assert.Equal(29, OwnerLimits.FestivalMaxDays - 1);
    }

    [Fact]
    public void Festival_state_is_optional_and_short()
    {
        Assert.True(_festival.Validate(Festival(state: null)).IsValid);
        Assert.True(_festival.Validate(Festival(state: "Uttarakhand")).IsValid);
        Assert.False(_festival.Validate(Festival(state: new string('s', 101))).IsValid);
    }

    [Fact]
    public void Special_price_body_needs_an_id_and_a_direction()
    {
        var create = new CreateSpecialPriceRequestValidator();
        Assert.True(create.Validate(new CreateSpecialPriceRequest { Id = Guid.NewGuid(), Direction = SpecialPriceDirections.Lower, Percent = 15 }).IsValid);
        Assert.False(create.Validate(new CreateSpecialPriceRequest { Id = Guid.Empty, Direction = SpecialPriceDirections.Lower, Percent = 15 }).IsValid);
        Assert.False(create.Validate(new CreateSpecialPriceRequest { Id = Guid.NewGuid(), Direction = "Down", Percent = 15 }).IsValid);
    }

    [Fact]
    public void Edits_need_a_version()
    {
        Assert.False(new UpdateSpecialPriceRequestValidator().Validate(new UpdateSpecialPriceRequest { Direction = SpecialPriceDirections.Higher, Version = 0 }).IsValid);
        Assert.True(new UpdateSpecialPriceRequestValidator().Validate(new UpdateSpecialPriceRequest { Direction = SpecialPriceDirections.Higher, Version = 2 }).IsValid);
        Assert.False(new PauseSpecialPriceRequestValidator().Validate(new PauseSpecialPriceRequest { Paused = true, Version = 0 }).IsValid);
        Assert.False(new LiveSpecialPriceRequestValidator().Validate(new LiveSpecialPriceRequest { Version = 0 }).IsValid);
        Assert.True(new LiveSpecialPriceRequestValidator().Validate(new LiveSpecialPriceRequest { Version = 1 }).IsValid);
    }
}
