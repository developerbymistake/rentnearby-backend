using FluentValidation;
using RentNearBy.Core.DTOs.Buddy;
using RentNearBy.Core.Models.Buddy;

namespace RentNearBy.Api.Validators.Buddy;

// S2: buddy user ids only (1..11 distinct, never empty), and a client request id.
public class TripInviteRequestValidator : AbstractValidator<TripInviteRequest>
{
    public TripInviteRequestValidator()
    {
        RuleFor(x => x.ClientRequestId).NotEmpty().WithErrorCode(BuddyErrorTypes.BadRequest).WithMessage("clientRequestId is required");
        RuleFor(x => x.UserIds).NotNull().WithErrorCode(BuddyErrorTypes.BadRequest)
            .Must(ids => ids is { Count: >= 1 and <= BuddyLimits.InviteIdsMax }).WithErrorCode(BuddyErrorTypes.BadRequest)
            .WithMessage($"Pick 1 to {BuddyLimits.InviteIdsMax} people")
            .Must(ids => ids == null || ids.Distinct().Count() == ids.Count).WithErrorCode(BuddyErrorTypes.BadRequest)
            .WithMessage("Each person once")
            .Must(ids => ids == null || ids.All(id => id != Guid.Empty)).WithErrorCode(BuddyErrorTypes.BadRequest)
            .WithMessage("Invalid buddy id");
    }
}
