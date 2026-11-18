using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using RentNearBy.Api.Handlers;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Models.TaxiRequests;

namespace RentNearBy.Tests.TaxiRequests;

// P25, contract section 1 exception 2: the old local taxi writers stop while a taxi request of the trip is active.
public class LegacyTaxiGuardTests
{
    private const string Guard = "TripPlanLock.LegacyTaxiBlock(await TripPlanLockReader.HasActiveRequestAsync(db, trip.Id))";
    private const string TripLock = "await TripGuards.LockTripAsync(db, trip.Id);";

    [Fact]
    public void Legacy_local_taxi_create_is_blocked_only_while_a_request_is_active()
    {
        Assert.Null(TripPlanLock.LegacyTaxiBlock(false));
        Assert.NotNull(TripPlanLock.LegacyTaxiBlock(true));
    }

    [Fact]
    public void Guard_answer_is_409_taxi_request_active_with_the_contract_message()
    {
        var refusal = TripPlanLock.LegacyTaxiBlock(true)!;
        Assert.Equal(409, refusal.StatusCode);
        Assert.Equal("TaxiRequestActive", refusal.ErrorType);
        Assert.Equal("This trip already has a taxi request. Update the app to see it.", refusal.Message);
        Assert.Null(refusal.Data);

        var result = TripHandlers.LockRefusal(refusal);
        Assert.Equal(409, ((IStatusCodeHttpResult)result).StatusCode);
        var body = Assert.IsType<ApiResponse<object>>(((IValueHttpResult)result).Value);
        Assert.Equal("409", body.Status);
        Assert.Equal(409, body.Code);
        Assert.Null(body.Data);
        Assert.Equal("TaxiRequestActive", body.Error!.Type);
        Assert.Equal("This trip already has a taxi request. Update the app to see it.", body.Error.Message);
    }

    [Fact]
    public void Both_legacy_local_taxi_writers_call_the_guard_after_the_trip_lock()
    {
        var source = Source();
        Assert.Equal(3, Count(source, Guard));

        var leads = Body(source, "CreateLeadsAsync");
        var lockAt = leads.IndexOf(TripLock, StringComparison.Ordinal);
        var guardAt = leads.IndexOf(Guard, lockAt, StringComparison.Ordinal);
        Assert.True(lockAt > 0, "CreateLeadsAsync lost its trip lock");
        Assert.True(guardAt > lockAt, "The lead guard must run after the trip lock");
        Assert.True(guardAt < leads.IndexOf("var fresh =", StringComparison.Ordinal), "The lead guard must run before any lead row is read or added");
        Assert.True(guardAt < leads.IndexOf("TripBulk.SaveRowsAsync(", StringComparison.Ordinal), "The lead guard must run before the rows are saved");

        var booking = Body(source, "CreateTaxiBooking");
        var journeyReturn = booking.IndexOf("return await CreateSegmentBookingAsync(", StringComparison.Ordinal);
        var begin = booking.IndexOf("await unitOfWork.BeginTransactionAsync();", StringComparison.Ordinal);
        var bookingLock = booking.IndexOf(TripLock, StringComparison.Ordinal);
        var bookingGuard = booking.IndexOf(Guard, StringComparison.Ordinal);
        Assert.True(journeyReturn > 0 && begin > journeyReturn, "The local branch transaction starts after the journey branch returned");
        // The trip lock is the first statement of the local transaction, the guard comes right after it.
        Assert.Matches(new Regex(@"BeginTransactionAsync\(\);\s*try\s*\{\s*await TripGuards\.LockTripAsync\(db, trip\.Id\);\s*if \(TripPlanLock\.LegacyTaxiBlock"),
            booking[begin..]);
        Assert.True(bookingLock > begin && bookingGuard > bookingLock);
    }

    [Fact]
    public void Lead_send_that_would_write_nothing_is_refused_before_the_driver_loop()
    {
        var leads = Body(Source(), "CreateLeadsAsync");
        var early = leads.IndexOf(Guard, StringComparison.Ordinal);
        Assert.True(early > leads.IndexOf("toProcess = requested", StringComparison.Ordinal), "The early guard runs after the replay split");
        Assert.True(early < leads.IndexOf("foreach (var listingId in toProcess)", StringComparison.Ordinal), "The early guard runs before the driver loop");
        Assert.True(early < leads.IndexOf("new RateLimitPermits(", StringComparison.Ordinal), "The early guard spends no quota");
        Assert.True(early < leads.IndexOf(TripLock, StringComparison.Ordinal), "The early guard is lock-free; writes check again under the lock");
        Assert.Matches(new Regex(@"if \(toProcess\.Count > 0\s*&& TripPlanLock\.LegacyTaxiBlock"), leads);
    }

    [Fact]
    public void Booking_refusal_gives_the_booking_permit_back()
    {
        var booking = Body(Source(), "CreateTaxiBooking");
        Assert.DoesNotContain("RateLimitedAsync(rateLimiter, http, $\"trip:booking:", booking);
        var take = booking.IndexOf("bookingPermit.TakeAsync($\"trip:booking:{callerId}\"", StringComparison.Ordinal);
        var guard = booking.IndexOf(Guard, StringComparison.Ordinal);
        Assert.True(take > 0 && guard > take);
        Assert.Matches(new Regex(@"LegacyTaxiBlock\([^\n]*\n\s*\{\s*await unitOfWork\.RollbackTransactionAsync\(\);\s*await bookingPermit\.ReleaseAllAsync\(\);\s*return TripHandlers\.LockRefusal\(requestActive\);"),
            booking[guard..]);
    }

    [Fact]
    public void Delete_never_skips_the_plan_lock_guard()
    {
        var delete = Body(File.ReadAllText(Path.Combine(RepoRoot(), "RentNearBy.Api", "Handlers", "TripHandlers.cs")), "Delete");
        Assert.Contains("ArgumentNullException.ThrowIfNull(db);", delete);
        Assert.DoesNotContain("if (db != null)", delete);
    }

    [Fact]
    public void Journey_branch_does_not_call_the_guard()
    {
        var segment = Body(Source(), "CreateSegmentBookingAsync");
        Assert.DoesNotContain("LegacyTaxiBlock", segment);
        Assert.DoesNotContain("HasActiveRequestAsync", segment);
    }

    [Fact]
    public void Trip_update_and_delete_take_the_trip_lock_before_reading_the_plan_lock()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "RentNearBy.Api", "Handlers", "TripHandlers.cs"));
        var update = Body(source, "Update");
        var begin = update.IndexOf("await unitOfWork.BeginTransactionAsync();", StringComparison.Ordinal);
        Assert.Matches(new Regex(@"BeginTransactionAsync\(\);\s*try\s*\{\s*//[^\n]*\n\s*await TripGuards\.LockTripAsync\(db, trip\.Id\);\s*var planLockNow = await TripPlanLockReader\.ReadAsync\(db, trip\.Id\);"),
            update[begin..]);
        Assert.True(update.IndexOf("TripPlanLockReader.ReadAsync(db, trip.Id)", StringComparison.Ordinal) < begin, "Update keeps its early check");

        var delete = Body(source, "Delete");
        var deleteLock = delete.IndexOf(TripLock, StringComparison.Ordinal);
        var deleteRead = delete.IndexOf("TripPlanLockReader.ReadAsync(db, trip.Id)", StringComparison.Ordinal);
        var closure = delete.IndexOf("closure.OnTripDeletedAsync", StringComparison.Ordinal);
        Assert.True(deleteLock > 0 && deleteRead > deleteLock && closure > deleteRead);
    }

    private static int Count(string text, string needle) => Regex.Matches(text, Regex.Escape(needle)).Count;

    private static string Source()
        => File.ReadAllText(Path.Combine(RepoRoot(), "RentNearBy.Api", "Handlers", "TripEnquiryHandlers.cs"));

    // From the method signature to the next member signature at class level.
    private static string Body(string source, string method)
    {
        var start = Regex.Match(source, @"\n    (public|private|internal) static async Task<IResult> " + method + @"\(");
        Assert.True(start.Success, $"{method} not found");
        var next = Regex.Match(source[(start.Index + 1)..], @"\n    (public|private|internal) static ");
        return next.Success ? source.Substring(start.Index, next.Index + 1) : source[start.Index..];
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RentNearBy.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found");
    }
}
