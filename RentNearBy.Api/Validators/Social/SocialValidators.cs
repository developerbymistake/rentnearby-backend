using FluentValidation;
using RentNearBy.Core.DTOs.Social;
using RentNearBy.Core.Models.Social;

namespace RentNearBy.Api.Validators.Social;

// Shape checks only: field rules with their typed answers (InvalidProfile / InvalidPost {field}, TextNotAllowed, TextTooLong)
// live in the social services so the app gets one error type per field.
// Coarse body-size guards only, far above the real field rules (SocialLimits.AboutMax / PlacesMax) that the services answer.
internal static class SocialShapeGuards
{
    public const int AboutMax = 1000;
    public const int PlacesMax = 100;
}

public class SocialOnboardingRequestValidator : AbstractValidator<SocialOnboardingRequest>
{
    public SocialOnboardingRequestValidator()
    {
        RuleFor(x => x.About).MaximumLength(SocialShapeGuards.AboutMax).WithMessage("About is too long.");
        RuleFor(x => x.Places).Must(p => p is null || p.Count <= SocialShapeGuards.PlacesMax).WithMessage("Too many places.");
    }
}

public class UpdateSocialProfileRequestValidator : AbstractValidator<UpdateSocialProfileRequest>
{
    public UpdateSocialProfileRequestValidator()
    {
        RuleFor(x => x.About).MaximumLength(SocialShapeGuards.AboutMax).WithMessage("About is too long.");
        RuleFor(x => x.Places).Must(p => p is null || p.Count <= SocialShapeGuards.PlacesMax).WithMessage("Too many places.");
    }
}

public class UpdatePrivacyRequestValidator : AbstractValidator<UpdatePrivacyRequest>
{
    public UpdatePrivacyRequestValidator()
    {
        RuleFor(x => x.MessagePolicy).Must(p => p is null || MessagePolicies.All.Contains(p)).WithMessage("Pick who can message you.");
    }
}

public class CreatePublicTripRequestValidator : AbstractValidator<CreatePublicTripRequest>
{
    public CreatePublicTripRequestValidator()
    {
        RuleFor(x => x.ClientTripId).NotEqual(Guid.Empty).WithMessage("Invalid client trip id.");
        RuleFor(x => x.CityKeys).NotNull().Must(k => k is { Count: >= 1 and <= SocialLimits.PostCitiesMax }).WithMessage("Pick 1 to 4 cities.");
        RuleFor(x => x.Text).MaximumLength(2000).WithMessage("Text is too long.");
    }
}

public class UpdatePublicPostRequestValidator : AbstractValidator<UpdatePublicPostRequest>
{
    public UpdatePublicPostRequestValidator()
    {
        RuleFor(x => x.Text).MaximumLength(2000).WithMessage("Text is too long.");
    }
}

public class SendJoinRequestValidator : AbstractValidator<SendJoinRequest>
{
    public SendJoinRequestValidator()
    {
        RuleFor(x => x.ClientRequestId).NotEqual(Guid.Empty).WithMessage("Invalid client request id.");
        RuleFor(x => x.Note).MaximumLength(SocialLimits.JoinNoteMax).WithMessage("Keep the note under 200 characters.");
    }
}

public class StartChatRequestValidator : AbstractValidator<StartChatRequest>
{
    public StartChatRequestValidator()
    {
        RuleFor(x => x.ToUserId).NotEqual(Guid.Empty).WithMessage("Invalid person.");
        RuleFor(x => x.ClientMessageId).NotEqual(Guid.Empty).WithMessage("Invalid client message id.");
    }
}

public class SendChatMessageRequestValidator : AbstractValidator<SendChatMessageRequest>
{
    public SendChatMessageRequestValidator()
    {
        RuleFor(x => x.ClientMessageId).NotEqual(Guid.Empty).WithMessage("Invalid client message id.");
    }
}

public class SocialReportRequestValidator : AbstractValidator<SocialReportRequest>
{
    public SocialReportRequestValidator()
    {
        RuleFor(x => x.Scope).Must(s => SocialReportTypes.All.Contains(s)).WithMessage("Scope must be TripPost or Message.");
        RuleFor(x => x.ReasonId).NotEqual(Guid.Empty).WithMessage("Pick a reason.");
        RuleFor(x => x.Note).MaximumLength(SocialLimits.ReportNoteMax).WithMessage("Keep the note under 300 characters.");
        RuleFor(x => x.TripId).NotNull().When(x => x.Scope == SocialReportTypes.TripPost).WithMessage("Trip is required.");
        RuleFor(x => x.RoomId).NotNull().When(x => x.Scope == SocialReportTypes.Message).WithMessage("Chat is required.");
        RuleFor(x => x.Seq).NotNull().GreaterThan(0).When(x => x.Scope == SocialReportTypes.Message).WithMessage("Message is required.");
    }
}

public class AdminIdentityRequestValidator : AbstractValidator<AdminIdentityRequest>
{
    public AdminIdentityRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(SocialLimits.IdentityReasonMax).WithMessage("Give a reason (up to 200 characters).");
        RuleFor(x => x.Gender).Must(g => g is null || SocialGenders.All.Contains(g)).WithMessage("Invalid gender.");
        RuleFor(x => x.BirthMonth).InclusiveBetween(1, 12).When(x => x.BirthMonth != null).WithMessage("Invalid birth month.");
        RuleFor(x => x.BirthYear).InclusiveBetween(SocialLimits.BirthYearMin, SocialLimits.BirthYearMax).When(x => x.BirthYear != null)
            .WithMessage("Invalid birth year.");
        RuleFor(x => x).Must(x => x.Gender != null || x.BirthMonth != null || x.BirthYear != null).WithMessage("Nothing to change.");
    }
}
