using System.Text.RegularExpressions;
using FluentValidation;
using RentNearBy.Core.DTOs.Owner.Quote;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Core.Models.Owner.Quote;

namespace RentNearBy.Api.Validators;

// Stay quotation master (ARCHITECTURE 3.3). Every message is a static string: no typed value (account number, IFSC, phone)
// is ever echoed into a 400 body. Values are checked as the service stores them (trimmed, empty = cleared).
public sealed class QuoteProfileRequestValidator : AbstractValidator<QuoteProfileRequest>
{
    public const string UnsafeText = "Remove special or hidden characters.";
    public const string SloganLong = "Keep the slogan under 80 characters.";
    public const string AddressLong = "Keep the address under 240 characters and 4 lines.";
    public const string PhoneBad = "Enter a 10 digit phone.";
    public const string EmailBad = "Enter a valid email.";
    public const string GstinBad = "Enter a valid GST number.";
    public const string TermsLong = "Keep the terms under 1200 characters.";
    public const string CancellationLong = "Keep the cancellation policy under 800 characters.";
    public const string BankModeBad = "Invalid request.";
    public const string BankMissing = "Add an account number or a UPI ID.";
    public const string AccountBad = "Enter an account number of 9 to 18 digits.";
    public const string IfscBad = "Enter a valid IFSC.";
    public const string HolderLong = "Keep the account holder name under 60 characters.";
    public const string BankLong = "Keep the bank and branch under 60 characters.";
    public const string UpiBad = "Enter a UPI ID like name@bank.";

    private static readonly Regex Phone = new(TripExecutionValidation.PhonePattern, RegexOptions.CultureInvariant);

    public QuoteProfileRequestValidator()
    {
        Text(x => x.Slogan, QuoteRules.SloganMax, SloganLong, multiLine: false);
        Text(x => x.Address, QuoteRules.AddressMax, AddressLong, multiLine: true);
        RuleFor(x => x.Address).Must(v => QuoteText.LineCount(QuoteText.Clean(v)) <= QuoteRules.AddressMaxLines).WithMessage(AddressLong);
        RuleFor(x => x.Phone).Must(v => QuoteText.Clean(v) is not { } p || Phone.IsMatch(p)).WithMessage(PhoneBad);
        Text(x => x.Email, QuoteRules.EmailMax, EmailBad, multiLine: false);
        RuleFor(x => x.Email).Must(v => QuoteText.Clean(v) is not { } e || QuoteText.IsValidEmail(e)).WithMessage(EmailBad);
        RuleFor(x => x.Gstin)
            .Must(v => QuoteText.Clean(v) is not { } g || StayRegistrationRules.IsValidGstin(StayRegistrationRules.NormalizeGstin(g)))
            .WithMessage(GstinBad);
        Text(x => x.TermsText, QuoteRules.TermsMax, TermsLong, multiLine: true);
        Text(x => x.CancellationText, QuoteRules.CancellationMax, CancellationLong, multiLine: true);

        RuleFor(x => x.BankMode).Must(m => m == null || QuoteBankModes.All.Contains(m)).WithMessage(BankModeBad);
        RuleFor(x => x.Bank).NotNull().WithMessage(BankMissing).When(x => x.BankMode == QuoteBankModes.Set);
        RuleFor(x => x.Bank!).SetValidator(new QuoteBankRequestValidator()).When(x => x.BankMode == QuoteBankModes.Set && x.Bank != null);
    }

    private void Text(System.Linq.Expressions.Expression<Func<QuoteProfileRequest, string?>> field, int max, string tooLong, bool multiLine)
    {
        RuleFor(field).Must(v => QuoteText.IsSafe(QuoteText.Clean(v), multiLine)).WithMessage(UnsafeText);
        RuleFor(field).Must(v => (QuoteText.Clean(v)?.Length ?? 0) <= max).WithMessage(tooLong);
    }
}

public sealed class QuoteBankRequestValidator : AbstractValidator<QuoteBankRequest>
{
    public QuoteBankRequestValidator()
    {
        RuleFor(x => x).Must(b => QuoteText.Clean(b.AccountNo) != null || QuoteText.Clean(b.Upi) != null)
            .OverridePropertyName("bank").WithMessage(QuoteProfileRequestValidator.BankMissing);
        RuleFor(x => x.AccountNo).Must(v => QuoteText.IsValidAccountNo(QuoteText.Clean(v)))
            .WithMessage(QuoteProfileRequestValidator.AccountBad)
            .When(b => QuoteText.Clean(b.AccountNo) != null || QuoteText.Clean(b.Ifsc) != null || QuoteText.Clean(b.Holder) != null);
        RuleFor(x => x.Ifsc).Must(v => QuoteText.IsValidIfsc(QuoteText.Clean(v)?.ToUpperInvariant()))
            .WithMessage(QuoteProfileRequestValidator.IfscBad)
            .When(b => QuoteText.Clean(b.AccountNo) != null || QuoteText.Clean(b.Ifsc) != null);
        RuleFor(x => x.Holder).Must(v => QuoteText.IsSafe(QuoteText.Clean(v), false) && (QuoteText.Clean(v)?.Length ?? 0) <= QuoteRules.BankTextMax)
            .WithMessage(QuoteProfileRequestValidator.HolderLong);
        RuleFor(x => x.Bank).Must(v => QuoteText.IsSafe(QuoteText.Clean(v), false) && (QuoteText.Clean(v)?.Length ?? 0) <= QuoteRules.BankTextMax)
            .WithMessage(QuoteProfileRequestValidator.BankLong);
        RuleFor(x => x.Branch).Must(v => QuoteText.IsSafe(QuoteText.Clean(v), false) && (QuoteText.Clean(v)?.Length ?? 0) <= QuoteRules.BankTextMax)
            .WithMessage(QuoteProfileRequestValidator.BankLong);
        RuleFor(x => x.Upi).Must(v => QuoteText.Clean(v) is not { } u || StayShareRules.IsValidUpi(u))
            .WithMessage(QuoteProfileRequestValidator.UpiBad);
    }
}

public sealed class QuoteTermsAcceptRequestValidator : AbstractValidator<QuoteTermsAcceptRequest>
{
    public QuoteTermsAcceptRequestValidator()
    {
        RuleFor(x => x.Version).NotNull().WithMessage("Invalid request.");
        RuleFor(x => x.AppVersion).Must(v => v == null || (v.Length <= QuoteRules.AppVersionMax && QuoteText.IsSafe(v, false)))
            .WithMessage("Invalid request.");
    }
}

public sealed class QuoteReserveRequestValidator : AbstractValidator<QuoteReserveRequest>
{
    public QuoteReserveRequestValidator()
    {
        RuleFor(x => x.DocumentId).Must(d => d is { } g && g != Guid.Empty).WithMessage("Invalid request.");
        RuleFor(x => x.Kind).Must(k => QuoteKinds.TryCode(k, out _)).WithMessage("Invalid request.");
    }
}

// Name rules of the stay edit screen (UpsertStayBasicRequestValidator) plus the quotation's bidi / control refusal.
public sealed class QuoteNameChangeRequestValidator : AbstractValidator<QuoteNameChangeRequest>
{
    public const string NameRequired = "Stay name is required";
    public const string NameLength = "Stay name must be 2 to 80 characters";
    public const string NoteLong = "Keep the reason under 200 characters.";

    public QuoteNameChangeRequestValidator()
    {
        RuleFor(x => x.Name)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage(NameRequired)
            .Must(n => n!.Trim().Length is >= 2 and <= 80).WithMessage(NameLength)
            .Must(n => QuoteText.IsSafe(n!.Trim(), false)).WithMessage(QuoteProfileRequestValidator.UnsafeText)
            .Must(n => !PhoneGuard.HasPhone(n)).WithMessage(OwnerErrorTypes.MessageOf(OwnerErrorTypes.PhoneNotAllowed));
        RuleFor(x => x.Note)
            .Must(v => (QuoteText.Clean(v)?.Length ?? 0) <= QuoteRules.NameChangeNoteMax).WithMessage(NoteLong)
            .Must(v => QuoteText.IsSafe(QuoteText.Clean(v), true)).WithMessage(QuoteProfileRequestValidator.UnsafeText);
    }
}
