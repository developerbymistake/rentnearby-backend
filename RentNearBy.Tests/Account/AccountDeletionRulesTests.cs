using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Tests.Account;

// Owner row O17: deletion is refused while anything is active, one specific reason per kind, fixed order.
public class AccountDeletionRulesTests
{
    private static readonly AccountActivity None = new(false, false, false, false, false, false);

    [Fact]
    public void Blocks_nothing_active_is_empty()
    {
        Assert.Empty(AccountDeletionRules.Blocks(None));
        Assert.True(AccountDeletionResult.Done.Deleted);
    }

    [Theory]
    [InlineData(AccountDeletionReasons.ActiveTaxiBooking, "You have an active taxi booking. You can delete your account after it ends.")]
    [InlineData(AccountDeletionReasons.ActiveStayBooking, "You have an active stay booking. You can delete your account after it ends.")]
    [InlineData(AccountDeletionReasons.SelectedTaxiOffer, "A traveller has chosen you. Confirm or decline the trip first.")]
    [InlineData(AccountDeletionReasons.OpenTaxiRequest, "You have an open taxi request. Cancel it first.")]
    [InlineData(AccountDeletionReasons.OpenStayEnquiry, "You have an open stay enquiry. Close it first.")]
    [InlineData(AccountDeletionReasons.LiveListing, "Take your listing offline first.")]
    public void Blocks_each_flag_gives_its_code_and_message(string code, string message)
    {
        var activity = code switch
        {
            AccountDeletionReasons.ActiveTaxiBooking => None with { ActiveTaxiBooking = true },
            AccountDeletionReasons.ActiveStayBooking => None with { ActiveStayBooking = true },
            AccountDeletionReasons.SelectedTaxiOffer => None with { SelectedTaxiOffer = true },
            AccountDeletionReasons.OpenTaxiRequest => None with { OpenTaxiRequest = true },
            AccountDeletionReasons.OpenStayEnquiry => None with { OpenStayEnquiry = true },
            _ => None with { LiveListing = true },
        };

        var block = Assert.Single(AccountDeletionRules.Blocks(activity));
        Assert.Equal(code, block.Code);
        Assert.Equal(message, block.Message);
        Assert.False(new AccountDeletionResult([block]).Deleted);
    }

    [Fact]
    public void Blocks_keep_the_fixed_order()
    {
        var all = new AccountActivity(true, true, true, true, true, true);
        Assert.Equal(AccountDeletionReasons.All, AccountDeletionRules.Blocks(all).Select(b => b.Code));
        Assert.Equal(
            ["ActiveTaxiBooking", "ActiveStayBooking", "SelectedTaxiOffer", "OpenTaxiRequest", "OpenStayEnquiry", "LiveListing"],
            AccountDeletionReasons.All);
    }

    [Fact]
    public void Reasons_all_have_a_message_and_no_duplicates()
    {
        Assert.Equal(AccountDeletionReasons.All.Count, AccountDeletionReasons.All.Distinct().Count());
        Assert.Equal(AccountDeletionReasons.All.OrderBy(c => c), AccountDeletionReasons.Messages.Keys.OrderBy(c => c));
        Assert.All(AccountDeletionReasons.Messages.Values, m => Assert.False(string.IsNullOrWhiteSpace(m)));
    }

    [Fact]
    public void Owner_texts_are_fixed()
    {
        Assert.Equal("Deleted user", AccountDeletionRules.DeletedUserName);
        Assert.Equal("AccountHasActiveItems", AccountDeletionRules.ErrorType);
    }

    [Fact]
    public void Reason_codes_are_not_taxi_request_error_types()
    {
        foreach (var code in AccountDeletionReasons.All.Append(AccountDeletionRules.ErrorType))
            Assert.DoesNotContain(code, TaxiRequestErrorTypes.All);
    }

    [Theory]
    [InlineData(UserRoles.Seeker, "Traveller")]
    [InlineData(UserRoles.StayOwner, "Stay Owner")]
    [InlineData(UserRoles.TaxiDriver, "Taxi Driver")]
    public void Role_names_match_the_seeded_roles(string key, string name)
    {
        Assert.Equal(name, UserRoles.NameOf(key));
    }

    [Fact]
    public void DeletedAccountRecord_role_is_required_max20_default_seeker()
    {
        using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=localhost;Database=design_only", o => o.UseNetTopologySuite())
            .Options);
        var role = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(DeletedAccountRecord))!.FindProperty(nameof(DeletedAccountRecord.Role))!;

        Assert.False(role.IsNullable);
        Assert.Equal(20, role.GetMaxLength());
        Assert.Equal(UserRoles.Seeker, role.GetDefaultValue());
        Assert.Equal(UserRoles.Seeker, new DeletedAccountRecord().Role);
    }
}
