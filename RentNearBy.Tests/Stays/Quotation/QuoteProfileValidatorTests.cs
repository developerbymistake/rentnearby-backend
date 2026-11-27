using RentNearBy.Api.Handlers.Owner;
using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.Owner.Quote;
using RentNearBy.Core.Models.Owner.Quote;

namespace RentNearBy.Tests.Stays.Quotation;

// Q6 / Q19 / ARCHITECTURE 3.3: every rule of the master, static messages only (no typed value in a 400), bidi and control
// characters refused, GSTIN check digit, bank intent.
public class QuoteProfileValidatorTests
{
    private const string ValidGstin = "27AAPFU0939F1ZV";
    private static readonly QuoteProfileRequestValidator Validator = new();

    private static QuoteProfileRequest Valid() => new()
    {
        Slogan = "Stay close to the forest",
        Address = "Village Dhikuli\nRamnagar, Nainital\nUttarakhand 244715",
        Phone = "9876543210",
        Email = "Stay@Example.com",
        Gstin = ValidGstin,
        TermsText = "Check-in from 12:00 pm, check-out by 11:00 am.\n50% advance confirms the booking.",
        CancellationText = "15 days or more: full advance refunded.",
        BankMode = QuoteBankModes.Keep,
    };

    private static QuoteBankRequest Bank() => new()
    {
        Holder = "Banyan Retreat", AccountNo = "123456789012", Ifsc = "sbin0001234", Bank = "State Bank of India", Branch = "Ramnagar",
        Upi = "banyan@sbi",
    };

    [Fact]
    public void A_full_valid_master_passes() => ValidationAssert.Valid(Validator.Validate(Valid()));

    [Fact]
    public void Every_field_may_be_empty()
        => ValidationAssert.Valid(Validator.Validate(new QuoteProfileRequest()));

    [Fact]
    public void Check_digit_of_the_gstin_is_verified()
    {
        Assert.True(StayRegistrationRules.IsValidGstin(ValidGstin));
        var bad = Valid();
        bad.Gstin = ValidGstin[..14] + (ValidGstin[14] == 'V' ? 'W' : 'V');
        ValidationAssert.HasMessage(Validator.Validate(bad), QuoteProfileRequestValidator.GstinBad);
    }

    [Fact]
    public void Gstin_is_normalised_before_the_check()
    {
        var spaced = Valid();
        spaced.Gstin = " 27aapfu0939f1zv ";
        ValidationAssert.Valid(Validator.Validate(spaced));
    }

    [Theory]
    [InlineData("‮evil")]
    [InlineData("Ali​ce")]
    [InlineData("na⁦me")]
    [InlineData("﻿stay")]
    public void Bidi_and_format_characters_are_refused_in_every_field(string value)
    {
        foreach (var set in new Action<QuoteProfileRequest>[]
                 {
                     r => r.Slogan = value, r => r.Address = value, r => r.TermsText = value, r => r.CancellationText = value,
                 })
        {
            var request = Valid();
            set(request);
            ValidationAssert.HasMessage(Validator.Validate(request), QuoteProfileRequestValidator.UnsafeText);
        }
    }

    // M-6: line separators, the Arabic letter mark, the soft hyphen and the invisible operators are refused too (char.IsControl is
    // false for all of them); U+2028 would otherwise pass a single-line field and the 4-line address rule.
    [Theory]
    [InlineData("a\u2028b")]
    [InlineData("a\u2029b")]
    [InlineData("a\u061Cb")]
    [InlineData("a\u00ADb")]
    [InlineData("a\u2060b")]
    [InlineData("a\u2064b")]
    [InlineData("a\u200Bb")]
    [InlineData("a\u202Eb")]
    [InlineData("a\u2066b")]
    [InlineData("a\uFEFFb")]
    public void Separator_and_invisible_format_characters_are_refused(string value)
    {
        Assert.True(QuoteText.HasBidi(value));
        Assert.False(QuoteText.IsSafe(value, multiLine: true));
        var request = Valid();
        request.Slogan = value;
        ValidationAssert.HasMessage(Validator.Validate(request), QuoteProfileRequestValidator.UnsafeText);
        var address = Valid();
        address.Address = "Line one" + value + "Line two";
        ValidationAssert.HasMessage(Validator.Validate(address), QuoteProfileRequestValidator.UnsafeText);
    }

    [Theory]
    [InlineData("Banyan Retreat, Ramnagar")]
    [InlineData("\u0928\u092E\u0938\u094D\u0924\u0947")]
    [InlineData("50% advance - 15 days")]
    public void Plain_text_and_indian_scripts_pass(string value) => Assert.False(QuoteText.HasBidi(value));

    [Fact]
    public void New_lines_only_where_multi_line_is_allowed()
    {
        var slogan = Valid();
        slogan.Slogan = "Line one\nLine two";
        ValidationAssert.HasMessage(Validator.Validate(slogan), QuoteProfileRequestValidator.UnsafeText);
        var tab = Valid();
        tab.TermsText = "a\tb";
        ValidationAssert.HasMessage(Validator.Validate(tab), QuoteProfileRequestValidator.UnsafeText);
    }

    [Fact]
    public void Lengths_and_address_lines_are_capped()
    {
        var r = Valid();
        r.Slogan = new string('a', QuoteRules.SloganMax + 1);
        ValidationAssert.HasMessage(Validator.Validate(r), QuoteProfileRequestValidator.SloganLong);
        r = Valid();
        r.Address = "1\n2\n3\n4\n5";
        ValidationAssert.HasMessage(Validator.Validate(r), QuoteProfileRequestValidator.AddressLong);
        r = Valid();
        r.TermsText = new string('t', QuoteRules.TermsMax + 1);
        ValidationAssert.HasMessage(Validator.Validate(r), QuoteProfileRequestValidator.TermsLong);
        r = Valid();
        r.CancellationText = new string('c', QuoteRules.CancellationMax + 1);
        ValidationAssert.HasMessage(Validator.Validate(r), QuoteProfileRequestValidator.CancellationLong);
    }

    [Theory]
    [InlineData("12345", false)]
    [InlineData("5876543210", false)]
    [InlineData("9876543210", true)]
    public void Phone_follows_the_ten_digit_pattern(string phone, bool ok)
    {
        var r = Valid();
        r.Phone = phone;
        Assert.Equal(ok, Validator.Validate(r).IsValid);
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("a@b")]
    public void Email_shape_is_checked(string email)
    {
        var r = Valid();
        r.Email = email;
        ValidationAssert.HasMessage(Validator.Validate(r), QuoteProfileRequestValidator.EmailBad);
    }

    [Fact]
    public void Bank_set_needs_a_block_with_an_account_or_a_upi()
    {
        var r = Valid();
        r.BankMode = QuoteBankModes.Set;
        ValidationAssert.HasMessage(Validator.Validate(r), QuoteProfileRequestValidator.BankMissing);
        r.Bank = new QuoteBankRequest { Holder = "Only a name" };
        ValidationAssert.HasMessage(Validator.Validate(r), QuoteProfileRequestValidator.AccountBad);
        r.Bank = new QuoteBankRequest { Upi = "banyan@sbi" };
        ValidationAssert.Valid(Validator.Validate(r));
        r.Bank = Bank();
        ValidationAssert.Valid(Validator.Validate(r));
    }

    [Theory]
    [InlineData("12345678", "SBIN0001234", QuoteProfileRequestValidator.AccountBad)]
    [InlineData("1234567890123456789", "SBIN0001234", QuoteProfileRequestValidator.AccountBad)]
    [InlineData("12345678901", "SBIN1001234", QuoteProfileRequestValidator.IfscBad)]
    [InlineData("12345678901", "SBI0001234", QuoteProfileRequestValidator.IfscBad)]
    public void Account_and_ifsc_shapes(string account, string ifsc, string message)
    {
        var r = Valid();
        r.BankMode = QuoteBankModes.Set;
        r.Bank = Bank();
        r.Bank.AccountNo = account;
        r.Bank.Ifsc = ifsc;
        ValidationAssert.HasMessage(Validator.Validate(r), message);
    }

    [Fact]
    public void Unknown_bank_mode_is_refused()
    {
        var r = Valid();
        r.BankMode = "Merge";
        Assert.False(Validator.Validate(r).IsValid);
    }

    [Fact]
    public void Keep_and_clear_ignore_the_bank_block()
    {
        var r = Valid();
        r.Bank = new QuoteBankRequest { AccountNo = "x" };
        r.BankMode = QuoteBankModes.Keep;
        ValidationAssert.Valid(Validator.Validate(r));
        r.BankMode = QuoteBankModes.Clear;
        ValidationAssert.Valid(Validator.Validate(r));
    }

    // S7: no 400 body ever echoes the account number, the IFSC or the phone the owner typed.
    [Fact]
    public void Messages_never_echo_a_typed_value()
    {
        var r = Valid();
        r.Phone = "99999";
        r.BankMode = QuoteBankModes.Set;
        r.Bank = new QuoteBankRequest { AccountNo = "77777777", Ifsc = "ZZZZ9ZZZZZZ", Upi = "x" };
        var result = Validator.Validate(r);
        Assert.False(result.IsValid);
        Assert.All(result.Errors, e =>
        {
            Assert.DoesNotContain("77777777", e.ErrorMessage);
            Assert.DoesNotContain("ZZZZ9ZZZZZZ", e.ErrorMessage);
            Assert.DoesNotContain("99999", e.ErrorMessage);
        });
    }

    [Fact]
    public void Validation_answer_is_typed_with_a_camel_case_field_map()
    {
        var r = Valid();
        r.Slogan = new string('a', QuoteRules.SloganMax + 1);
        r.BankMode = QuoteBankModes.Set;
        r.Bank = new QuoteBankRequest { AccountNo = "1" };
        var map = QuoteHttp.CamelPath("Bank.AccountNo");
        Assert.Equal("bank.accountNo", map);
        Assert.Equal("slogan", QuoteHttp.CamelPath("Slogan"));
        Assert.Equal("request", QuoteHttp.CamelPath(""));
        Assert.False(Validator.Validate(r).IsValid);
    }

    [Fact]
    public void Name_change_rules_match_the_stay_edit_screen_plus_bidi()
    {
        var v = new QuoteNameChangeRequestValidator();
        ValidationAssert.Valid(v.Validate(new QuoteNameChangeRequest { Name = "The Banyan Retreat", Note = "Spelling fix" }));
        ValidationAssert.HasMessage(v.Validate(new QuoteNameChangeRequest { Name = "" }), QuoteNameChangeRequestValidator.NameRequired);
        ValidationAssert.HasMessage(v.Validate(new QuoteNameChangeRequest { Name = "A" }), QuoteNameChangeRequestValidator.NameLength);
        ValidationAssert.HasMessage(v.Validate(new QuoteNameChangeRequest { Name = "Banyan‮" }), QuoteProfileRequestValidator.UnsafeText);
        ValidationAssert.HasMessage(v.Validate(new QuoteNameChangeRequest { Name = "Banyan", Note = new string('n', 201) }),
            QuoteNameChangeRequestValidator.NoteLong);
    }

    [Fact]
    public void Reserve_accepts_only_a_document_id_and_a_known_kind()
    {
        var v = new QuoteReserveRequestValidator();
        ValidationAssert.Valid(v.Validate(new QuoteReserveRequest { DocumentId = Guid.NewGuid(), Kind = QuoteKinds.Proforma }));
        Assert.False(v.Validate(new QuoteReserveRequest { DocumentId = Guid.Empty, Kind = QuoteKinds.Quotation }).IsValid);
        Assert.False(v.Validate(new QuoteReserveRequest { DocumentId = Guid.NewGuid(), Kind = "Invoice" }).IsValid);
        Assert.False(v.Validate(new QuoteReserveRequest { Kind = QuoteKinds.Quotation }).IsValid);
    }

    // Q5: the request model has no name field, so a client-sent name / businessName is never bound.
    [Fact]
    public void Profile_request_has_no_name_property()
    {
        var names = typeof(QuoteProfileRequest).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain("Name", names);
        Assert.DoesNotContain("BusinessName", names);
        Assert.DoesNotContain(typeof(QuoteReserveRequest).GetProperties(), p => p.Name is "Name" or "BusinessName" or "Number");
    }
}
