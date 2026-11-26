using System.Data;
using System.Text.RegularExpressions;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Services.TaxiRequests;

namespace RentNearBy.Tests.TaxiRequests;

// Reads backend source files for the guard tests of B2-6.
internal static class TaxiSource
{
    public static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RentNearBy.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found");
    }

    public static string Read(params string[] parts) => File.ReadAllText(Path.Combine([Root(), .. parts]));

    public static IEnumerable<string> ProductionFiles()
        => new[] { "RentNearBy.Api", "RentNearBy.Infrastructure", "RentNearBy.Core" }
            .SelectMany(p => Directory.EnumerateFiles(Path.Combine(Root(), p), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"));

    // The text of one member, from its signature to the next member declared at the same indentation.
    public static string Member(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signature}' not found");
        var next = Regex.Match(source[(start + signature.Length)..], @"\n    (public|private|internal) ");
        return next.Success ? source.Substring(start, signature.Length + next.Index) : source[start..];
    }

    public static int At(string text, string marker)
    {
        var i = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(i >= 0, $"'{marker}' not found");
        return i;
    }
}

public class DriverCalendarOrderTests
{
    private sealed class RecordingCalendar(DriverLockRow? driver, TaxiDateRange? clash) : IDriverCalendarSql
    {
        public List<string> Calls { get; } = [];
        public bool Locked { get; private set; }

        public Task<DriverLockRow?> LockDriverAsync(Guid driverListingId, CancellationToken ct)
        {
            Calls.Add("lock");
            Locked = driver is not null;
            return Task.FromResult(driver);
        }

        public Task<TaxiDateRange?> FindOverlapAsync(Guid driverListingId, TaxiDateRange range, Guid? exceptEnquiryId, CancellationToken ct)
        {
            Assert.True(Locked, "overlap query sent before the driver row lock was held");
            Calls.Add("overlap");
            return Task.FromResult(clash);
        }
    }

    private static readonly DriverLockRow Driver = new(Guid.NewGuid(), Guid.NewGuid(), TaxiStatuses.Approved, false);
    private static readonly TaxiDateRange Range = new(new DateOnly(2026, 12, 18), new DateOnly(2026, 12, 21));

    [Fact]
    public async Task Overlap_query_is_sent_after_the_driver_lock()
    {
        var clash = new TaxiDateRange(new DateOnly(2026, 12, 20), new DateOnly(2026, 12, 24));
        var busy = new RecordingCalendar(Driver, clash);
        var result = await DriverCalendar.LockAndCheckAsync(busy, Driver.Id, Range, null, CancellationToken.None);
        Assert.Equal(["lock", "overlap"], busy.Calls);
        Assert.Equal(DriverCheckOutcome.Overlap, result.Outcome);
        Assert.Equal(clash, result.Clash);

        var free = new RecordingCalendar(Driver, null);
        Assert.Equal(DriverCheckOutcome.Free, (await DriverCalendar.LockAndCheckAsync(free, Driver.Id, Range, null, CancellationToken.None)).Outcome);
        Assert.Equal(["lock", "overlap"], free.Calls);

        var gone = new RecordingCalendar(null, null);
        Assert.Equal(DriverCheckOutcome.DriverMissing, (await DriverCalendar.LockAndCheckAsync(gone, Driver.Id, Range, null, CancellationToken.None)).Outcome);
        Assert.Equal(["lock"], gone.Calls);
    }

    [Fact]
    public void Lock_statement_locks_the_driver_row_and_the_overlap_statement_is_a_plain_read()
    {
        Assert.Contains("FROM \"TaxiListings\" WHERE \"Id\" = @driver FOR UPDATE", DriverCalendar.LockDriverSql);
        Assert.DoesNotContain("FOR UPDATE", DriverCalendar.OverlapSql);
        Assert.DoesNotContain("SKIP LOCKED", DriverCalendar.LockDriverSql);

        // The real calendar sends each statement as its own query on the caller's context; nothing is cached or tracked.
        var source = TaxiSource.Read("RentNearBy.Infrastructure", "Services", "TaxiRequests", "DriverCalendar.cs");
        var find = TaxiSource.Member(source, "public async Task<TaxiDateRange?> FindOverlapAsync(");
        Assert.Contains("SqlQueryRaw<RangeRow>(DriverCalendar.OverlapSql", find);
        Assert.DoesNotContain("Local", find);
    }

    [Fact]
    public void Confirm_transaction_is_read_committed()
    {
        Assert.Equal(IsolationLevel.ReadCommitted, DriverCalendar.ConfirmIsolation);
        var calendar = TaxiSource.Read("RentNearBy.Infrastructure", "Services", "TaxiRequests", "DriverCalendar.cs");
        Assert.Contains("db.Database.BeginTransactionAsync(ConfirmIsolation, ct)", calendar);
        Assert.DoesNotMatch(@"IsolationLevel\.(RepeatableRead|Serializable|Snapshot)", calendar);

        var booking = TaxiSource.Read("RentNearBy.Infrastructure", "Services", "TaxiRequests", "TaxiBookingService.cs");
        var confirm = TaxiSource.Member(booking, "private async Task<TaxiActionResult> ConfirmUnitAsync(");
        Assert.Contains("DriverCalendar.BeginConfirmTransactionAsync(db, ct)", confirm);
        Assert.DoesNotContain("Database.BeginTransactionAsync(", confirm);
        Assert.DoesNotMatch(@"IsolationLevel\.(RepeatableRead|Serializable|Snapshot)", booking);
    }

    [Fact]
    public void New_confirm_locks_request_then_driver_then_checks_before_any_write()
    {
        var booking = TaxiSource.Read("RentNearBy.Infrastructure", "Services", "TaxiRequests", "TaxiBookingService.cs");
        var confirm = TaxiSource.Member(booking, "private async Task<TaxiActionResult> ConfirmUnitAsync(");
        var tx = TaxiSource.At(confirm, "DriverCalendar.BeginConfirmTransactionAsync");
        var request = TaxiSource.At(confirm, "store.LockRequestAsync");
        var check = TaxiSource.At(confirm, "DriverCalendar.LockAndCheckAsync");
        var book = TaxiSource.At(confirm, "BookAsync(");
        Assert.True(tx < request && request < check && check < book);
    }

    [Fact]
    public void Legacy_confirm_takes_the_same_lock_first()
    {
        var source = TaxiSource.Read("RentNearBy.Api", "Handlers", "TripLeadHandlers.cs");
        var confirm = TaxiSource.Member(source, "public static async Task<IResult> Confirm(");
        var tx = TaxiSource.At(confirm, "DriverCalendar.BeginConfirmTransactionAsync(db)");
        var check = TaxiSource.At(confirm, "DriverCalendar.LockAndCheckAsync(");
        var write = TaxiSource.At(confirm, "e.Status = TripEnquiryStatuses.ConfirmedByBoth;");
        Assert.True(tx < check && check < write, "legacy confirm must lock the driver row and check overlaps before it confirms");
        Assert.Contains("new EfDriverCalendarSql(db)", confirm);
        Assert.Contains("TripEnquiryErrorTypes.DayAlreadyBooked", source);
        Assert.DoesNotContain("unitOfWork.BeginTransactionAsync", confirm);
        Assert.DoesNotContain("SaveAsync(unitOfWork", confirm);
        Assert.Contains("public static async Task<IResult> Confirm(\n        Guid id, ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db, IRateLimitService rateLimiter,\n        ITripNotifier notifier, HttpContext httpContext, ILoggerFactory loggers)",
            source.Replace("\r\n", "\n"));
        Assert.DoesNotContain("FeedCache.BumpAsync", confirm);
    }
}
