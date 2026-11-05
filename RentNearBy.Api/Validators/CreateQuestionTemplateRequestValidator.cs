using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class CreateQuestionTemplateRequestValidator : AbstractValidator<CreateQuestionTemplateRequest>
{
    public CreateQuestionTemplateRequestValidator()
    {
        RuleFor(x => x.Key)
            .NotEmpty().WithMessage("Key is required")
            .MaximumLength(50)
            .Matches("^[a-z0-9_]+$").WithMessage("Key must be lowercase snake_case (e.g. is_available)");

        RuleFor(x => x.ListingType)
            .Must(t => t is "Stay").WithMessage("ListingType must be 'Stay'");

        RuleFor(x => x.StayTypeId)
            .Null().When(x => x.ListingType != "Stay")
            .WithMessage("StayTypeId can only be set when ListingType is 'Stay'");

        RuleFor(x => x.QuestionText)
            .NotEmpty().WithMessage("Question text is required")
            .MaximumLength(200);

        RuleFor(x => x.AnswerOptionsJson)
            .NotEmpty().WithMessage("At least one answer option is required")
            .Must(AnswerOptionsValidation.IsValid)
            .WithMessage("Answer options must be a JSON array of objects, each with a non-empty 'key' and 'text', with no duplicate keys");
    }
}
