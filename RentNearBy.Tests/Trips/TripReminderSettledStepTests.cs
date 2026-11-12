using System.Reflection;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Services;
using Xunit;

namespace RentNearBy.Tests.Trips;

public class TripReminderSettledStepTests
{
    private static string Sql(string name)
        => (string)typeof(TripReminderRunner).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    [Theory]
    [InlineData("ShortlistFinal")]
    [InlineData("ShortlistActive")]
    [InlineData("EnquiryFinal")]
    public void Reminder_filters_skip_a_step_with_a_confirmed_or_booked_enquiry(string name)
    {
        var sql = Sql(name);
        Assert.Contains("NOT EXISTS", sql);
        Assert.Contains("'ConfirmedByBoth','BookedByTraveller'", sql);
    }

    [Fact]
    public void Enquiry_final_push_needs_the_full_clock()
        => Assert.Contains($"interval '{TripExecutionRules.EnquiryHours} hours'", Sql("EnquiryFinal"));

    [Theory]
    [InlineData("ShortlistFinal")]
    [InlineData("ShortlistActive")]
    public void Shortlist_reminders_need_the_listing_to_exist_approved_and_on(string name)
    {
        var sql = Sql(name);
        Assert.Contains("\"StayListings\"", sql);
        Assert.Contains("\"TaxiListings\"", sql);
        Assert.Contains("sl.\"IsDeleted\" = false", sql);
        Assert.Contains("sl.\"Status\" = 'Approved'", sql);
        Assert.Contains("sl.\"AcceptingGuests\" = true", sql);
        Assert.Contains("tl.\"IsDeleted\" = false", sql);
        Assert.Contains("tl.\"Status\" = 'Approved'", sql);
        Assert.Contains("tl.\"IsAvailable\" = true", sql);
        Assert.Contains("tl.\"LocalDayRate\" IS NOT NULL", sql);
    }

    [Fact]
    public void Host_reminders_ignore_the_travellers_mute_but_not_completion()
    {
        var sql = (string)typeof(TripReminderRunner).GetField("HostTripOk", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        Assert.DoesNotContain("MutedAt", sql);
        Assert.Contains("'Completed'", sql);
    }
}
