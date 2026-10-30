using FluentValidation.TestHelper;
using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Tests.Validation;

public class UpsertPlanRequestValidatorTests
{
    private static readonly UpsertPlanRequestValidator Validator = new();

    private static TestValidationResult<UpsertPlanRequest> Validate(UpsertPlanRequest request) => Validator.TestValidate(request);

    private static UpsertPlanRequest WithPrices(params PlanPriceInput[] prices) => Requests.UpsertPlan() with { Prices = [.. prices] };

    private static UpsertPlanRequest WithFeatures(params PlanFeatureInput[] features) => Requests.UpsertPlan() with { Features = [.. features] };

    [Fact]
    public void ValidRequest_NoErrors()
        => Validate(Requests.UpsertPlan()).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void NullFeaturesAndPricesAndNoTrial_Valid()
        => Validate(Requests.UpsertPlan() with { Features = null, Prices = null, PlayProductId = null, Tagline = null })
            .ShouldNotHaveAnyValidationErrors();

    [Theory]
    [InlineData("Car")]
    [InlineData("room")]
    [InlineData("")]
    public void FeatureKey_Unknown_Error(string featureKey)
        => Validate(Requests.UpsertPlan() with { FeatureKey = featureKey }).ShouldHaveValidationErrorFor(x => x.FeatureKey);

    [Theory]
    [InlineData("PLUS_1", true)]
    [InlineData("plus", true)]
    [InlineData("plus-1", false)]
    [InlineData("PLUS 1", false)]
    [InlineData("", false)]
    public void Code_LettersDigitsUnderscore(string code, bool valid)
    {
        var result = Validate(Requests.UpsertPlan() with { Code = code });

        if (valid) result.ShouldNotHaveValidationErrorFor(x => x.Code);
        else result.ShouldHaveValidationErrorFor(x => x.Code);
    }

    [Theory]
    [InlineData(40, true)]
    [InlineData(41, false)]
    public void Code_Length(int length, bool valid)
        => Assert.Equal(valid, Validate(Requests.UpsertPlan() with { Code = new string('C', length) }).IsValid);

    [Theory]
    [InlineData(60, true)]
    [InlineData(61, false)]
    [InlineData(0, false)]
    public void Name_Length(int length, bool valid)
        => Assert.Equal(valid, Validate(Requests.UpsertPlan() with { Name = new string('n', length) }).IsValid);

    [Theory]
    [InlineData(120, true)]
    [InlineData(121, false)]
    public void Tagline_Length(int length, bool valid)
        => Assert.Equal(valid, Validate(Requests.UpsertPlan() with { Tagline = new string('t', length) }).IsValid);

    [Theory]
    [InlineData(30, true)]
    [InlineData(31, false)]
    [InlineData(-1, false)]
    public void TrialDays_Range(int days, bool valid)
    {
        var result = Validate(Requests.UpsertPlan() with { TrialDays = days });

        if (valid) result.ShouldNotHaveValidationErrorFor(x => x.TrialDays);
        else result.ShouldHaveValidationErrorFor(x => x.TrialDays);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(10_000, true)]
    [InlineData(10_001, false)]
    [InlineData(-1, false)]
    public void SortOrder_Range(int sortOrder, bool valid)
        => Assert.Equal(valid, Validate(Requests.UpsertPlan() with { SortOrder = sortOrder }).IsValid);

    [Theory]
    [InlineData("room.plus", true)]
    [InlineData("room_plus", true)]
    [InlineData("", true)]
    [InlineData(" ", true)]
    [InlineData(null, true)]
    [InlineData("Room.Plus", false)]
    [InlineData("_room", false)]
    [InlineData("room-plus", false)]
    public void PlayProductId_Pattern(string? productId, bool valid)
    {
        var result = Validate(Requests.UpsertPlan() with { PlayProductId = productId });

        if (valid) result.ShouldNotHaveValidationErrorFor(x => x.PlayProductId);
        else result.ShouldHaveValidationErrorFor(x => x.PlayProductId);
    }

    [Fact]
    public void Features_DuplicateKey_Error()
        => ValidationAssert.HasMessage(
            Validate(WithFeatures(new PlanFeatureInput(PlanFeatureKeys.Priority, 1), new PlanFeatureInput(PlanFeatureKeys.Priority, 2))),
            "Each feature key can appear once");

    [Fact]
    public void Features_UnknownKey_Error()
    {
        var result = Validate(WithFeatures(new PlanFeatureInput("Bogus", 1)));

        ValidationAssert.HasErrorOn(result, "Features");
        ValidationAssert.HasMessage(result, $"Feature key must be one of: {string.Join(", ", PlanFeatureKeys.All)}");
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(10_000, true)]
    [InlineData(10_001, false)]
    [InlineData(-1, false)]
    public void Features_ValueRange(int value, bool valid)
    {
        var result = Validate(WithFeatures(new PlanFeatureInput(PlanFeatureKeys.ListingLimit, value)));

        if (valid) result.ShouldNotHaveAnyValidationErrors();
        else ValidationAssert.HasErrorOn(result, "Features");
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(1_000_000, true)]
    [InlineData(0, false)]
    [InlineData(1_000_001, false)]
    public void Prices_PriceInrRange(int priceInr, bool valid)
    {
        var result = Validate(WithPrices(new PlanPriceInput(1, priceInr, null, true, "cycle-1m")));

        if (valid) result.ShouldNotHaveAnyValidationErrors();
        else ValidationAssert.HasErrorOn(result, "Prices");
    }

    [Fact]
    public void Prices_InvalidCycle_Error()
        => ValidationAssert.HasMessage(Validate(WithPrices(new PlanPriceInput(2, 299, null))), "CycleMonths must be 1, 3 or 12");

    [Fact]
    public void Prices_DuplicateCycle_Error()
        => ValidationAssert.HasMessage(
            Validate(WithPrices(new PlanPriceInput(1, 299, null, true, "cycle-1m"), new PlanPriceInput(1, 199, null, true, "cycle-1m-b"))),
            "Each billing cycle can appear once");

    [Fact]
    public void Prices_TwoTrialOffers_Error()
        => ValidationAssert.HasMessage(
            Validate(WithPrices(
                new PlanPriceInput(1, 299, null, true, "cycle-1m", "trial-7d"),
                new PlanPriceInput(3, 799, null, true, "cycle-3m", "trial-3m"))),
            "Only one price can have a trial offer");

    [Fact]
    public void Prices_TrialOfferOnThreeMonths_Error()
        => ValidationAssert.HasMessage(
            Validate(WithPrices(new PlanPriceInput(3, 799, null, true, "cycle-3m", "trial-7d"))),
            "The trial offer goes on the 1-month price only");

    [Fact]
    public void Prices_TrialOfferWithoutBasePlan_Error()
        => ValidationAssert.HasMessage(
            Validate(WithPrices(new PlanPriceInput(1, 299, null, true, null, "trial-7d"))),
            "A trial offer id needs a base plan id");

    [Fact]
    public void Prices_TrialOfferWithZeroTrialDays_Error()
        => ValidationAssert.HasMessage(Validate(Requests.UpsertPlan() with { TrialDays = 0 }), "A trial offer id needs trial days above 0");

    [Fact]
    public void Prices_ZeroTrialDaysWithoutOffer_Valid()
        => Validate(WithPrices(new PlanPriceInput(1, 299, null, true, "cycle-1m")) with { TrialDays = 0 }).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void Prices_DuplicateBasePlanId_Error()
        => ValidationAssert.HasMessage(
            Validate(WithPrices(new PlanPriceInput(1, 299, null, true, "cycle-x"), new PlanPriceInput(3, 799, null, true, " cycle-x "))),
            "Each Play base plan id can appear once");

    [Theory]
    [InlineData("cycle-1m", true)]
    [InlineData("1m", true)]
    [InlineData("cycle_1m", false)]
    [InlineData("Cycle-1m", false)]
    [InlineData("-cycle", false)]
    public void Prices_BasePlanIdPattern(string basePlanId, bool valid)
    {
        var result = Validate(WithPrices(new PlanPriceInput(1, 299, null, true, basePlanId)));

        if (valid) result.ShouldNotHaveAnyValidationErrors();
        else ValidationAssert.HasErrorOn(result, "Prices");
    }

    [Theory]
    [InlineData(64, true)]
    [InlineData(65, false)]
    public void Prices_RazorpayPlanIdLength(int length, bool valid)
        => Assert.Equal(valid, Validate(WithPrices(new PlanPriceInput(1, 299, new string('r', length), true, "cycle-1m"))).IsValid);
}
