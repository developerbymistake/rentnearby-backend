using RentNearBy.Api.Validators.Social;
using RentNearBy.Core.DTOs.Social;

namespace RentNearBy.Tests.Social;

// Spec 23.9: the admin identity correction may carry only a name (admin can always change it).
public class AdminIdentityValidatorTests
{
    private static bool Valid(AdminIdentityRequest r) => new AdminIdentityRequestValidator().Validate(r).IsValid;

    [Fact]
    public void A_name_alone_is_a_valid_correction() => Assert.True(Valid(new AdminIdentityRequest { Name = "Asha Rawat", Reason = "Fake name" }));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_name_is_refused(string name) => Assert.False(Valid(new AdminIdentityRequest { Name = name, Reason = "Fake name" }));

    [Fact]
    public void A_name_over_100_characters_is_refused()
        => Assert.False(Valid(new AdminIdentityRequest { Name = new string('a', 101), Reason = "Fake name" }));

    [Fact]
    public void Nothing_to_change_is_still_refused() => Assert.False(Valid(new AdminIdentityRequest { Reason = "Nothing" }));
}
