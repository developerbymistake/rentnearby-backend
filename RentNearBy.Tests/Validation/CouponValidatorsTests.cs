using FluentValidation.TestHelper;
using RentNearBy.Api.Validators;

namespace RentNearBy.Tests.Validation;

public class CouponValidatorsTests
{
    private static readonly CouponRuleValidator Rules = new();
    private static readonly CreateCouponRequestValidator Create = new();
    private static readonly UpdateCouponRequestValidator Update = new();

    [Theory]
    [InlineData("ABC", true)]
    [InlineData("ABC-1_x", true)]
    [InlineData("AB", false)]
    [InlineData("AB C", false)]
    [InlineData("AB@C", false)]
    [InlineData("", false)]
    public void Create_Code(string code, bool valid)
    {
        var result = Create.TestValidate(Requests.CreateCoupon() with { Code = code });

        if (valid) result.ShouldNotHaveAnyValidationErrors();
        else result.ShouldHaveValidationErrorFor(x => x.Code);
    }

    [Theory]
    [InlineData(32, true)]
    [InlineData(33, false)]
    public void Create_CodeLength(int length, bool valid)
        => Assert.Equal(valid, Create.TestValidate(Requests.CreateCoupon() with { Code = new string('C', length) }).IsValid);

    [Fact]
    public void Create_RuleErrors_Propagated()
    {
        var result = Create.TestValidate(Requests.CreateCoupon() with { Kind = CouponKinds.Percent, Value = 101 });

        ValidationAssert.HasMessage(result, "A percent coupon needs a value from 1 to 100");
    }

    [Fact]
    public void Update_RuleErrors_Propagated()
    {
        var result = Update.TestValidate(Requests.UpdateCoupon() with { Kind = CouponKinds.FreePlan, AutoApply = true });

        ValidationAssert.HasMessage(result, "A free-plan coupon cannot be auto-applied");
    }

    [Fact]
    public void Update_ValidRequest_NoErrors()
        => Update.TestValidate(Requests.UpdateCoupon()).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void Rules_ValidSample_NoErrors()
        => Rules.TestValidate(Requests.CouponRule()).ShouldNotHaveAnyValidationErrors();

    [Theory]
    [InlineData("Bogus", false)]
    [InlineData("percent", false)]
    [InlineData(CouponKinds.Percent, true)]
    [InlineData(CouponKinds.Flat, true)]
    [InlineData(CouponKinds.FreePlan, true)]
    public void Rules_Kind(string kind, bool valid)
    {
        var result = Rules.TestValidate(Requests.CouponRule() with { Kind = kind });

        if (valid) result.ShouldNotHaveValidationErrorFor(x => x.Kind);
        else result.ShouldHaveValidationErrorFor(x => x.Kind);
    }

    [Theory]
    [InlineData(CouponKinds.Percent, 0, false)]
    [InlineData(CouponKinds.Percent, 1, true)]
    [InlineData(CouponKinds.Percent, 100, true)]
    [InlineData(CouponKinds.Percent, 101, false)]
    [InlineData(CouponKinds.Flat, 0, false)]
    [InlineData(CouponKinds.Flat, 1, true)]
    [InlineData(CouponKinds.Flat, 1_000_000, true)]
    [InlineData(CouponKinds.Flat, 1_000_001, false)]
    [InlineData(CouponKinds.FreePlan, 0, true)]
    public void Rules_ValueRangePerKind(string kind, int value, bool valid)
    {
        var result = Rules.TestValidate(Requests.CouponRule() with { Kind = kind, Value = value });

        if (valid) result.ShouldNotHaveValidationErrorFor(x => x.Value);
        else result.ShouldHaveValidationErrorFor(x => x.Value);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Rules_FreePlanAutoApply(bool autoApply, bool valid)
    {
        var result = Rules.TestValidate(Requests.CouponRule() with { Kind = CouponKinds.FreePlan, Value = 0, AutoApply = autoApply });

        if (valid) result.ShouldNotHaveAnyValidationErrors();
        else result.ShouldHaveValidationErrorFor(x => x.AutoApply).WithErrorMessage("A free-plan coupon cannot be auto-applied");
    }

    [Fact]
    public void Rules_PercentAutoApply_Allowed()
        => Rules.TestValidate(Requests.CouponRule() with { AutoApply = true }).ShouldNotHaveAnyValidationErrors();

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(0, false)]
    public void Rules_PerUserLimitMustBeOne(int limit, bool valid)
    {
        var result = Rules.TestValidate(Requests.CouponRule() with { PerUserLimit = limit });

        if (valid) result.ShouldNotHaveValidationErrorFor(x => x.PerUserLimit);
        else result.ShouldHaveValidationErrorFor(x => x.PerUserLimit).WithErrorMessage("PerUserLimit must be 1");
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(1, true)]
    [InlineData(null, true)]
    public void Rules_OptionalPositiveAmounts(int? amount, bool valid)
    {
        var result = Rules.TestValidate(Requests.CouponRule() with
        {
            MaxDiscountInr = amount,
            MinPlanPriceInr = amount,
            MaxTotalRedemptions = amount,
        });

        if (valid)
        {
            result.ShouldNotHaveAnyValidationErrors();
        }
        else
        {
            result.ShouldHaveValidationErrorFor(x => x.MaxDiscountInr);
            result.ShouldHaveValidationErrorFor(x => x.MinPlanPriceInr);
            result.ShouldHaveValidationErrorFor(x => x.MaxTotalRedemptions);
        }
    }

    [Theory]
    [InlineData("Car", false)]
    [InlineData("room", false)]
    [InlineData(null, true)]
    [InlineData(ListingKinds.Room, true)]
    [InlineData(ListingKinds.Taxi, true)]
    public void Rules_FeatureKey(string? featureKey, bool valid)
    {
        var result = Rules.TestValidate(Requests.CouponRule() with { FeatureKey = featureKey });

        if (valid) result.ShouldNotHaveValidationErrorFor(x => x.FeatureKey);
        else result.ShouldHaveValidationErrorFor(x => x.FeatureKey);
    }

    public enum Window { FromOnly, UntilOnly, UntilAfterFrom, UntilEqualsFrom, UntilBeforeFrom }

    [Theory]
    [InlineData(Window.FromOnly, true)]
    [InlineData(Window.UntilOnly, true)]
    [InlineData(Window.UntilAfterFrom, true)]
    [InlineData(Window.UntilEqualsFrom, false)]
    [InlineData(Window.UntilBeforeFrom, false)]
    public void Rules_ValidityWindow(Window window, bool valid)
    {
        var from = Dates.Utc(2026, 11, 1);
        (DateTime? validFrom, DateTime? validUntil) = window switch
        {
            Window.FromOnly => ((DateTime?)from, (DateTime?)null),
            Window.UntilOnly => (null, from),
            Window.UntilAfterFrom => (from, from.AddDays(1)),
            Window.UntilEqualsFrom => (from, from),
            _ => (from, from.AddDays(-1)),
        };

        var result = Rules.TestValidate(Requests.CouponRule() with { ValidFrom = validFrom, ValidUntil = validUntil });

        if (valid) result.ShouldNotHaveAnyValidationErrors();
        else result.ShouldHaveValidationErrorFor(x => x.ValidUntil).WithErrorMessage("ValidUntil must be after ValidFrom");
    }

    [Theory]
    [InlineData(80, true)]
    [InlineData(81, false)]
    public void Rules_CampaignLabelLength(int length, bool valid)
    {
        var result = Rules.TestValidate(Requests.CouponRule() with { CampaignLabel = new string('l', length) });

        if (valid) result.ShouldNotHaveAnyValidationErrors();
        else result.ShouldHaveValidationErrorFor(x => x.CampaignLabel);
    }
}
