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
            .Must(t => t is "Room" or "Plot" or "Both").WithMessage("ListingType must be 'Room', 'Plot' or 'Both'");

        RuleFor(x => x.RoomTypeId)
            .Null().When(x => x.ListingType != "Room")
            .WithMessage("RoomTypeId can only be set when ListingType is 'Room'");

        RuleFor(x => x.PlotTypeId)
            .Null().When(x => x.ListingType != "Plot")
            .WithMessage("PlotTypeId can only be set when ListingType is 'Plot'");

        RuleFor(x => x.QuestionText)
            .NotEmpty().WithMessage("Question text is required")
            .MaximumLength(200);

        RuleFor(x => x.AnswerOptionsJson)
            .NotEmpty().WithMessage("At least one answer option is required");
    }
}
