using RentNearBy.Core.Models.Owner;
using RentNearBy.Tests.Trips;

namespace RentNearBy.Tests.Owner.Journeys;

// FB-07 (D-33, JT-B-TX-1 step "Free, first day") and FB-21 / FB-24 (D-13, JT-B-LC-4).
public class ListingSwitchJourneyTests
{
    private static string Src(params string[] parts) => File.ReadAllText(Path.Combine([CompletionCounterGuardTests.Root(), .. parts]));

    [Fact]
    public void New_taxi_is_offline()
        => Assert.False(ListingSwitchRules.TaxiAvailableOnCreate);

    [Theory]
    [InlineData(false, true, true, false)]   // first approval: stays offline whatever the stored value
    [InlineData(false, true, false, false)]
    [InlineData(true, true, true, true)]     // re-approval of a live edit keeps the driver's switch
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]   // suspended owner: off
    public void Approval_never_turns_a_taxi_on(bool everApproved, bool ownerActive, bool current, bool expected)
        => Assert.Equal(expected, ListingSwitchRules.TaxiAvailableAfterApproval(everApproved, ownerActive, current));

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void Suspend_turns_off_and_reactivation_keeps_it_off(bool isActive, bool current, bool expected)
        => Assert.Equal(expected, ListingSwitchRules.AfterStatusChange(isActive, current));

    [Fact]
    public void Taxi_create_and_approve_use_the_rule()
    {
        Assert.Contains("IsAvailable = ListingSwitchRules.TaxiAvailableOnCreate", Src("RentNearBy.Api", "Handlers", "TaxiHandlers.cs"));
        var approve = Src("RentNearBy.Api", "Handlers", "AdminTaxiHandlers.cs");
        var rule = approve.IndexOf("ListingSwitchRules.TaxiAvailableAfterApproval(taxi.EverApproved", StringComparison.Ordinal);
        var flag = approve.IndexOf("taxi.EverApproved = true;", StringComparison.Ordinal);
        Assert.True(rule > 0 && flag > rule); // read before the flag is set
    }

    [Fact]
    public void Reactivation_never_writes_the_switch_and_suspend_closes_unanswered_leads()
    {
        var src = Src("RentNearBy.Api", "Handlers", "AdminHandlers.cs");
        var status = src[src.IndexOf("public static async Task<IResult> UpdateUserStatus", StringComparison.Ordinal)..];
        Assert.DoesNotContain("SetProperty(x => x.AcceptingGuests, request.IsActive)", status);
        Assert.DoesNotContain("SetProperty(x => x.IsAvailable, request.IsActive)", status);
        Assert.Contains("TripLeadSilence.ClosePausedDriverLeadsAsync", status);
        Assert.Contains("AfterCommitAsync(leadPushes)", status);
    }
}
