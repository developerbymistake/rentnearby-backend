using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class UpdateQuestionTemplateRequestValidator : AbstractValidator<UpdateQuestionTemplateRequest>
{
    public UpdateQuestionTemplateRequestValidator()
    {
        RuleFor(x => x.QuestionText)
            .MaximumLength(200)
            .When(x => x.QuestionText != null);

        RuleFor(x => x.AnswerOptionsJson)
            .NotEmpty()
            .Must(AnswerOptionsValidation.IsValid)
            .WithMessage("Answer options must be a JSON array of objects, each with a non-empty 'key' and 'text', with no duplicate keys")
            .When(x => x.AnswerOptionsJson != null);
    }
}
