using RentNearBy.Core.Models.Owner;
using RentNearBy.Core.Models.Owner.Quote;
using RentNearBy.Infrastructure.Services.Owner.Quote;

namespace RentNearBy.Tests.Stays.Quotation;

// Pure rules of the quotation server: numbering display, kinds, image paths, masking, D-6, the mixed-script flag, limit lines,
// the own rate-limit buckets and the server constants of BUILD_PLAN 4.4.
public class QuoteRulesTests
{
    [Fact]
    public void Server_constants_match_the_contract()
    {
        Assert.Equal(5, QuoteRules.MaxFreePerWindow);
        Assert.True(QuoteRules.PreviewWatermark);
        Assert.Equal(1, QuoteRules.MinTemplateVersion);
        Assert.Equal(TimeSpan.FromHours(24), QuoteRules.FreeWindow);
        Assert.Equal(180, QuoteRules.IssueRetentionDays);
    }

    [Theory]
    [InlineData(QuoteKinds.QuotationCode, 42, "Q-0042")]
    [InlineData(QuoteKinds.ProformaCode, 7, "PI-0007")]
    [InlineData(QuoteKinds.QuotationCode, 12345, "Q-12345")]
    public void Display_is_prefix_and_four_digits(short kind, int number, string expected)
        => Assert.Equal(expected, QuoteKinds.Display(kind, number));

    [Fact]
    public void Kinds_round_trip_and_unknown_is_refused()
    {
        Assert.True(QuoteKinds.TryCode("Quotation", out var q));
        Assert.True(QuoteKinds.TryCode("Proforma", out var p));
        Assert.Equal((0, 1), (q, p));
        Assert.Equal("Proforma", QuoteKinds.NameOf(p));
        Assert.False(QuoteKinds.TryCode("quotation", out _));
        Assert.False(QuoteKinds.TryCode(null, out _));
    }

    [Fact]
    public void Image_paths_live_in_the_owner_quote_folder_and_belong_checks_owner_and_kind()
    {
        var user = Guid.NewGuid();
        var logo = QuoteImagePaths.NewPublicId(user, QuoteImageKind.Logo);
        Assert.StartsWith($"bakhli/user_{user}/quote/logo/", logo);
        Assert.True(QuoteImagePaths.Belongs(logo, user, QuoteImageKind.Logo));
        Assert.False(QuoteImagePaths.Belongs(logo, user, QuoteImageKind.Signature));
        Assert.False(QuoteImagePaths.Belongs(logo, Guid.NewGuid(), QuoteImageKind.Logo));
        Assert.False(QuoteImagePaths.Belongs($"bakhli/user_{user}/profile/x", user, QuoteImageKind.Logo));
        Assert.False(QuoteImagePaths.Belongs(null, user, QuoteImageKind.Logo));
        Assert.Equal("authenticated", QuoteImagePaths.DeliveryType);
    }

    [Theory]
    [InlineData("logo", true)]
    [InlineData("signature", true)]
    [InlineData("Logo", false)]
    [InlineData("../logo", false)]
    [InlineData("stamp", false)]
    public void Only_two_image_kinds_exist(string value, bool ok) => Assert.Equal(ok, QuoteImageKinds.TryParse(value, out _));

    [Theory]
    [InlineData("banyanretreat@okaxis", "ba***@okaxis")]
    [InlineData("a@sbi", "a***@sbi")]
    [InlineData("noat", "***")]
    [InlineData(null, null)]
    public void Upi_is_masked(string? upi, string? masked) => Assert.Equal(masked, QuoteText.MaskUpi(upi));

    [Theory]
    [InlineData("123456789012", "9012")]
    [InlineData("123", null)]
    [InlineData(null, null)]
    public void Account_shows_only_the_last_four(string? account, string? last4) => Assert.Equal(last4, QuoteText.Last4(account));

    [Theory]
    [InlineData(null, "27AAPFU0939F1ZV", true)]
    [InlineData("27AAPFU0939F1ZV", null, true)]
    [InlineData("27AAPFU0939F1ZV", "27aapfu0939f1zv", true)]
    [InlineData("27AAPFU0939F1ZV", "05ABCDE1234F1Z5", false)]
    public void Gstin_must_match_the_stay_one_when_the_stay_has_one(string? quote, string? stay, bool allowed)
        => Assert.Equal(allowed, QuoteGstinRule.Allowed(quote, stay));

    [Theory]
    [InlineData("The Banyan Retreat", false)]
    [InlineData("Тaj Palace", true)]
    [InlineData("Banyan Тадж", false)]
    [InlineData("Ρaradise", true)]
    [InlineData("बरगद Retreat", false)]
    [InlineData(null, false)]
    public void Mixed_latin_with_cyrillic_or_greek_inside_one_word_is_flagged(string? name, bool mixed)
        => Assert.Equal(mixed, StayNameScript.IsMixed(name));

    [Fact]
    public void Limit_lines_come_from_the_limit_never_a_typed_number()
    {
        Assert.Equal("You have used 2 quotations on the Free plan this month.", QuoteLimitMessages.For(2, canUpgrade: true));
        Assert.Equal("You have used all 30 quotations this month.", QuoteLimitMessages.For(30, canUpgrade: false));
        Assert.Equal("You have used all 1 quotation this month.", QuoteLimitMessages.For(1, canUpgrade: false));
    }

    [Fact]
    public void Own_buckets_are_photo_ten_an_hour_reserve_thirty_an_hour_name_three_a_day()
    {
        var u = Guid.NewGuid();
        Assert.Equal(($"quote:photo:{u}", 10, TimeSpan.FromHours(1)), (QuoteRateLimits.Photo(u).Key, QuoteRateLimits.Photo(u).Max, QuoteRateLimits.Photo(u).Window));
        Assert.Equal(($"quote:reserve:{u}", 30, TimeSpan.FromHours(1)), (QuoteRateLimits.Reserve(u).Key, QuoteRateLimits.Reserve(u).Max, QuoteRateLimits.Reserve(u).Window));
        Assert.Equal(($"quote:name:{u}", 3, TimeSpan.FromDays(1)), (QuoteRateLimits.NameChange(u).Key, QuoteRateLimits.NameChange(u).Max, QuoteRateLimits.NameChange(u).Window));
        Assert.Null(QuoteRateLimits.Reserve(u).Attempt);
    }

    [Theory]
    [InlineData(QuoteErrors.GstinMismatch, 400)]
    [InlineData(QuoteErrors.TermsVersionUnknown, 400)]
    [InlineData(QuoteErrors.StayNotFound, 404)]
    [InlineData(QuoteErrors.ImageMissing, 404)]
    [InlineData(QuoteErrors.StayNotApproved, 409)]
    [InlineData(QuoteErrors.TermsNotAccepted, 409)]
    [InlineData(QuoteErrors.MonthLimit, 409)]
    [InlineData(QuoteErrors.BankUnavailable, 503)]
    [InlineData(QuoteErrors.PhotoBusy, 503)]
    [InlineData(QuoteErrors.PhotoInvalid, 400)]
    [InlineData(QuoteErrors.PhotoTooLarge, 400)]
    public void Error_types_map_to_the_contract_status(string type, int status) => Assert.Equal(status, QuoteErrors.StatusOf(type));

    [Fact]
    public void Usage_of_an_older_month_reads_zero()
    {
        Assert.Equal(7, StayQuoteUsageReader.UsedThisMonth(202611, 7, 202611));
        Assert.Equal(0, StayQuoteUsageReader.UsedThisMonth(202610, 7, 202611));
        Assert.Equal(0, StayQuoteUsageReader.UsedThisMonth(null, null, 202611));
    }

    // Q18: nothing the server sends for a document carries the brand.
    [Fact]
    public void No_brand_word_in_any_quotation_server_string()
    {
        foreach (var message in new[]
                 {
                     QuoteErrors.MessageOf(QuoteErrors.MonthLimit), QuoteErrors.MessageOf(QuoteErrors.StayNotApproved),
                     QuoteLimitMessages.For(2, true), QuoteLimitMessages.For(30, false),
                 })
            Assert.DoesNotContain("bakhli", message, StringComparison.OrdinalIgnoreCase);
    }
}
