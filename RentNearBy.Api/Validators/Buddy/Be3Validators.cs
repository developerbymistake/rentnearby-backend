using FluentValidation;
using FluentValidation.Results;
using RentNearBy.Core.DTOs.Buddy;
using RentNearBy.Core.Models.Buddy;

namespace RentNearBy.Api.Validators.Buddy;

// ErrorCode carries the BuddyErrorTypes value the handler answers with.
public class SaveExpenseRequestValidator : AbstractValidator<SaveExpenseRequestDto>
{
    public SaveExpenseRequestValidator()
    {
        RuleFor(x => x.OpId).NotEmpty().WithErrorCode(BuddyErrorTypes.BadRequest).WithMessage("opId is required");
        RuleFor(x => x.BaseVersion).GreaterThanOrEqualTo(0).WithErrorCode(BuddyErrorTypes.BadRequest);
        RuleFor(x => x).Custom((request, context) =>
        {
            var error = TripExpenseRules.CheckInput(request, out _);
            if (error is not null) context.AddFailure(new ValidationFailure(string.Empty, BuddyErrorTypes.MessageOf(error)) { ErrorCode = error });
        });
    }
}

public class ShareWithRequestValidator : AbstractValidator<ShareWithRequestDto>
{
    public ShareWithRequestValidator()
    {
        RuleFor(x => x.OpId).NotEmpty().WithErrorCode(BuddyErrorTypes.BadRequest);
        RuleFor(x => x.UserId).NotEmpty().WithErrorCode(BuddyErrorTypes.BadRequest);
        RuleFor(x => x.Items).NotNull().WithErrorCode(BuddyErrorTypes.BadRequest)
            .Must(i => i is { Count: >= 1 and <= BuddyLimits.ShareWithMaxItems }).WithErrorCode(BuddyErrorTypes.BadRequest)
            .WithMessage($"Pick 1 to {BuddyLimits.ShareWithMaxItems} expenses");
        RuleForEach(x => x.Items).ChildRules(item =>
        {
            item.RuleFor(i => i.ExpenseId).NotEmpty().WithErrorCode(BuddyErrorTypes.BadRequest);
            item.RuleFor(i => i.BaseVersion).GreaterThanOrEqualTo(1).WithErrorCode(BuddyErrorTypes.BadRequest);
        });
    }
}

public class SkipSuggestionRequestValidator : AbstractValidator<SkipSuggestionRequestDto>
{
    public SkipSuggestionRequestValidator()
    {
        RuleFor(x => x.Kind).Equal(ExpenseLinkKinds.Stay).WithErrorCode(BuddyErrorTypes.BadRequest);
        RuleFor(x => x.SourceId).NotEmpty().WithErrorCode(BuddyErrorTypes.BadRequest);
    }
}
