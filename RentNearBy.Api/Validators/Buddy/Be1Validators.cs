using FluentValidation;
using RentNearBy.Core.DTOs.Buddy;
using RentNearBy.Core.Models.Buddy;

namespace RentNearBy.Api.Validators.Buddy;

public class FindBuddyRequestValidator : AbstractValidator<FindBuddyRequest>
{
    public FindBuddyRequestValidator()
    {
        RuleFor(x => x.Query).NotEmpty().WithMessage("Enter a username.")
            .MaximumLength(BuddyLimits.FindQueryMax).WithMessage("Enter a username.");
        RuleFor(x => x.Source).Must(s => s is null || BuddyRequestSources.All.Contains(s)).WithMessage("Invalid source.");
    }
}

public class SendBuddyRequestValidator : AbstractValidator<SendBuddyRequest>
{
    public SendBuddyRequestValidator()
    {
        RuleFor(x => x.ToUserId).NotEmpty().WithMessage("Pick a person.");
        RuleFor(x => x.ClientRequestId).NotEmpty().WithMessage("Missing request id.");
        RuleFor(x => x.Source).Must(s => s is null || BuddyRequestSources.All.Contains(s)).WithMessage("Invalid source.");
    }
}

public class BuddySeenRequestValidator : AbstractValidator<BuddySeenRequest>
{
    public BuddySeenRequestValidator()
    {
        RuleFor(x => x.UserIds).NotNull().Must(ids => ids.Count is >= 1 and <= BuddyLimits.SeenIdsMax)
            .WithMessage($"Send 1 to {BuddyLimits.SeenIdsMax} people.");
    }
}

public class BlockPersonRequestValidator : AbstractValidator<BlockPersonRequest>
{
    public BlockPersonRequestValidator()
    {
        RuleFor(x => x.Context).Must(c => c is null || UserReportContexts.All.Contains(c)).WithMessage("Invalid context.");
        When(x => x.AlsoReport is not null, () =>
        {
            RuleFor(x => x.AlsoReport!.Note).MaximumLength(BuddyLimits.ReportNoteMax)
                .WithMessage($"Keep the note under {BuddyLimits.ReportNoteMax} characters.");
        });
    }
}

public class ReportPersonRequestValidator : AbstractValidator<ReportPersonRequest>
{
    public ReportPersonRequestValidator()
    {
        RuleFor(x => x.Note).MaximumLength(BuddyLimits.ReportNoteMax)
            .WithMessage($"Keep the note under {BuddyLimits.ReportNoteMax} characters.");
        RuleFor(x => x.Context).Must(c => c is null || UserReportContexts.All.Contains(c)).WithMessage("Invalid context.");
    }
}
