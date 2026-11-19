using System.Text.RegularExpressions;
using RentNearBy.Api.Handlers.TaxiRequests;
using RentNearBy.Core.Entities.TaxiRequests;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Services.TaxiRequests;

namespace RentNearBy.Tests.TaxiRequests;

// Owner rows O6, O15 and O9 (OWNER_DECISIONS_USECASES.md): behaviour that is already built and must not drift.
public class TaxiOwnerPinTests
{
    private static string Source(params string[] parts) => TaxiSource.Read(parts).Replace("\r\n", "\n");

    // O6: while the chosen driver has his 15 minutes to confirm, Ask more stays closed.
    [Theory]
    [InlineData(1, 3, false)]
    [InlineData(3, 3, false)]
    [InlineData(1, 3, true)]
    [InlineData(8, 9, false)]
    [InlineData(0, 3, true)]
    public void O6_ask_more_is_closed_while_selected(int accepted, int target, bool exhausted)
    {
        Assert.False(TaxiDispatchRules.CanAskMore(RequestStates.Selected, accepted, target, exhausted));
        Assert.DoesNotContain(RequestStates.Selected, RequestStates.Dispatching);
    }

    [Fact]
    public void O6_ask_more_route_and_sql_refuse_a_selected_request()
    {
        Assert.Contains($"WHERE \"Id\" = @id AND \"Status\" IN ('{RequestStates.Open}', '{RequestStates.Collecting}')", TaxiRequestService.AskMoreSql);
        Assert.DoesNotContain(RequestStates.Selected, TaxiRequestService.AskMoreSql);

        var service = Source("RentNearBy.Infrastructure", "Services", "TaxiRequests", "TaxiRequestService.cs");
        var askMore = TaxiSource.Member(service, "public async Task<TaxiRequestResult> AskMoreAsync(");
        Assert.Contains("if (!RequestStates.Dispatching.Contains(locked.Status)) return Step.StateInvalid(locked);", askMore);
    }

    // O6: an accept from a ring still out counts on a Selected request and leaves its state (and the selection) alone.
    [Fact]
    public void O6_accept_from_a_ring_still_out_counts_while_selected()
    {
        var sql = TaxiOfferService.AcceptCountSql;
        Assert.Contains($"\"Status\" IN ('{RequestStates.Open}', '{RequestStates.Collecting}', '{RequestStates.Selected}')", sql);
        Assert.Contains($"CASE WHEN \"Status\" = '{RequestStates.Open}' THEN '{RequestStates.Collecting}' ELSE \"Status\" END", sql);
        Assert.DoesNotContain("SelectedOfferId", sql);
        Assert.DoesNotContain("TaxiRequests", TaxiOfferService.AcceptOfferSql);
    }

    // O6: the traveller sees the backup accepts next to the chosen driver, but cannot pick one or ask for more.
    [Fact]
    public void O6_backup_accepts_are_listed_but_not_selectable_while_selected()
    {
        var selected = Guid.NewGuid();
        var backup = Guid.NewGuid();
        var request = new TaxiRequest
        {
            Id = Guid.NewGuid(), TripId = Guid.NewGuid(), TravellerUserId = Guid.NewGuid(), Status = RequestStates.Selected,
            WantSightseeing = true, Seats = 4, Travellers = 2, DateFrom = new DateOnly(2026, 12, 18), DateTo = new DateOnly(2026, 12, 20),
            AcceptedCount = 2, AcceptTarget = 3, SelectedOfferId = selected, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        TaxiViewOffer Offer(Guid id, string status, int seq, DateTime? confirmBy = null)
            => new(id, status, seq, confirmBy, "Driver", "+910000000000", "Car", 4, 0, 0, 0);
        var offers = new[]
        {
            Offer(selected, OfferStates.Selected, 1, DateTime.UtcNow.AddMinutes(10)),
            Offer(backup, OfferStates.Accepted, 2),
            Offer(Guid.NewGuid(), OfferStates.Ringing, 3),
        };

        var dto = TaxiRequestViews.Build(new TaxiViewInput(request, "Trip", 3, 3, offers, false, null, null, new DateOnly(2026, 12, 1)));

        Assert.False(dto.CanAskMore);
        Assert.False(dto.StillAsking);
        var listed = dto.Drivers.ToDictionary(d => d.OfferId);
        Assert.Equal(OfferStates.Selected, listed[selected].State);
        Assert.Equal(OfferStates.Accepted, listed[backup].State);
        Assert.False(listed[backup].CanSelect);
        Assert.NotNull(listed[backup].Phone);
    }

    // O15: business onboarding only gates NEW registrations; dispatch never reads the flag (only district IsActive).
    [Fact]
    public void O15_dispatch_never_reads_business_onboarding()
    {
        Assert.Contains("d.\"IsActive\"", TaxiCandidateQuery.StaticRule);
        foreach (var rule in new[] { TaxiCandidateQuery.StaticRule, TaxiCandidateQuery.RequestRule, TaxiCandidateQuery.FreeRule, TaxiCandidateQuery.CityPoolRule })
            Assert.DoesNotContain("BusinessOnboarding", rule);

        var dispatch = TaxiSource.ProductionFiles()
            .Where(f => f.Contains($"{Path.DirectorySeparatorChar}TaxiRequests{Path.DirectorySeparatorChar}"))
            .ToList();
        Assert.NotEmpty(dispatch);
        Assert.DoesNotContain(dispatch, f => File.ReadAllText(f).Contains("BusinessOnboarding"));
    }

    [Fact]
    public void O15_turning_onboarding_off_writes_only_the_district_flag()
    {
        var admin = Source("RentNearBy.Api", "Handlers", "AdminHandlers.cs");
        foreach (var signature in new[] { "public static async Task<IResult> ToggleDistrictOnboarding(", "public static async Task<IResult> BulkToggleStateOnboarding(" })
        {
            var toggle = TaxiSource.Member(admin, signature);
            Assert.Contains("BusinessOnboardingEnabled", toggle);
            Assert.DoesNotContain("TaxiListings", toggle);
            Assert.DoesNotContain("TaxiRequest", toggle);
            Assert.DoesNotContain("IsAvailable", toggle);
            Assert.DoesNotContain("Status", toggle);
        }
    }

    // O9: one booking writer, inside the confirm transaction, after the driver lock and overlap check; it never commits.
    [Fact]
    public void O9_the_only_booking_insert_runs_inside_the_locked_confirm_transaction()
    {
        var writers = TaxiSource.ProductionFiles()
            .Where(f => File.ReadAllText(f) is var t && (t.Contains("Set<TaxiBooking>().Add(") || t.Contains("INSERT INTO \"TaxiBookings\"")))
            .Select(Path.GetFileName)
            .ToList();
        Assert.Equal(["TaxiBookingService.cs"], writers);

        var booking = Source("RentNearBy.Infrastructure", "Services", "TaxiRequests", "TaxiBookingService.cs");
        var book = TaxiSource.Member(booking, "private async Task<(TaxiClosureResult, TaxiActionResult)> BookAsync(");
        Assert.Contains("db.Set<TaxiBooking>().Add(booking);", book);
        Assert.DoesNotContain("CommitAsync", book);
        Assert.DoesNotContain("BeginTransaction", book);
        Assert.Single(Regex.Matches(booking, @"\bBookAsync\(request, offer, range, ct\)"));

        var confirm = TaxiSource.Member(booking, "private async Task<TaxiActionResult> ConfirmUnitAsync(");
        Assert.Single(Regex.Matches(confirm, "BeginConfirmTransactionAsync"));
        var check = TaxiSource.At(confirm, "DriverCalendar.LockAndCheckAsync(");
        var bookAt = TaxiSource.At(confirm, "BookAsync(request, offer, range, ct)");
        var commitAfterBook = confirm.IndexOf("await tx.CommitAsync(ct);", bookAt, StringComparison.Ordinal);
        Assert.True(check < bookAt && bookAt < commitAfterBook, "check, booking insert and commit must be one transaction in this order");
    }
}
