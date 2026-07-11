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
            .When(x => x.AnswerOptionsJson != null);
    }
}
