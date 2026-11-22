using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.Models.Owner;

namespace RentNearBy.Tests.Owner.Journeys;

// FB-28 (D-16, JT-B-SEC-1): public stay text saved live never carries a phone number. Room names and rule values are
// fixed lists (check constraints), so the description is the only free text.
public class StayTextPhoneGuardTests
{
    private static IEnumerable<FluentValidation.Results.ValidationFailure> DescriptionErrors(string? description)
        => new UpsertStayBasicRequestValidator().Validate(new UpsertStayBasicRequest { Description = description })
            .Errors.Where(e => e.PropertyName == nameof(UpsertStayBasicRequest.Description));

    [Theory]
    [InlineData("Call 98765 43210 for bookings")]
    [InlineData("WhatsApp +91-98765-43210")]
    public void Phone_in_the_description_is_refused_with_its_type(string text)
    {
        var error = Assert.Single(DescriptionErrors(text));
        Assert.Equal(OwnerErrorTypes.PhoneNotAllowed, error.ErrorCode);
    }

    [Theory]
    [InlineData("Rs 2,500 per night, 3 rooms, 10 minutes from the lake")]
    [InlineData(null)]
    public void Prices_and_small_numbers_pass(string? text) => Assert.Empty(DescriptionErrors(text));
}
