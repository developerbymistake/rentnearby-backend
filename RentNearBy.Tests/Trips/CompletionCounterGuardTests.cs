using System.Reflection;
using System.Text.RegularExpressions;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.Trips;

// FB-W1 / FB-W2 / FB-W3: one writer of the lifetime counters, never exposed outside the admin user DTO.
public class CompletionCounterGuardTests
{
    internal static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RentNearBy.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Solution root not found");
    }

    private static IEnumerable<string> ProductionSources()
    {
        var sep = Path.DirectorySeparatorChar;
        return new[] { "RentNearBy.Api", "RentNearBy.Infrastructure", "RentNearBy.Core" }
            .SelectMany(p => Directory.EnumerateFiles(Path.Combine(Root(), p), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{sep}bin{sep}") && !f.Contains($"{sep}obj{sep}") && !f.Contains($"{sep}Migrations{sep}"));
    }

    // A write is a C# assignment, an EF SetProperty, or an SQL SET of the column.
    private static readonly Regex Write = new(
        "(CompletedTripsCount|CompletedBookingsCount)\\s*\\+?=(?!=)|SetProperty\\([^)]*Completed(Trips|Bookings)Count|\"Completed(Trips|Bookings)Count\\\\?\"\\s*=",
        RegexOptions.Compiled);

    [Fact]
    public void Counters_are_written_only_by_the_counter_class()
    {
        var offenders = ProductionSources()
            .Where(f => !f.EndsWith("TripCompletionCounter.cs", StringComparison.Ordinal))
            .Where(f => Write.IsMatch(File.ReadAllText(f)))
            .Select(Path.GetFileName)
            .ToList();
        Assert.Empty(offenders);
    }

    [Theory]
    [InlineData("user.CompletedTripsCount = 0;")]
    [InlineData("u.CompletedBookingsCount += 1;")]
    [InlineData(".SetProperty(u => u.CompletedTripsCount, 0)")]
    [InlineData("UPDATE \"Users\" SET \"CompletedBookingsCount\" = 0")]
    public void Guard_pattern_sees_every_kind_of_write(string code) => Assert.Matches(Write, code);

    [Fact]
    public void Counter_class_holds_every_write()
    {
        Assert.Contains("\"CompletedTripsCount\" = u.\"CompletedTripsCount\" + 1", TripCompletionCounter.TripSql);
        Assert.Contains("\"CompletedBookingsCount\" = u.\"CompletedBookingsCount\" + g.n", TripCompletionCounter.HostsSql);
        Assert.Contains("\"CompletedBookingsCount\" = u.\"CompletedBookingsCount\" + 1", TripCompletionCounter.TaxiBookingSql);
    }

    [Fact]
    public void Guards_claim_each_row_once_and_values_are_bound()
    {
        foreach (var sql in new[] { TripCompletionCounter.TripSql, TripCompletionCounter.HostsSql, TripCompletionCounter.TaxiBookingSql })
        {
            Assert.Contains("\"CompletionCountedAt\" IS NULL", sql);
            Assert.DoesNotContain("{", sql);
        }
        Assert.Contains("t.\"EndDate\" IS NOT NULL AND t.\"EndDate\" < @today", TripCompletionCounter.TripSql);
        Assert.Contains("e.\"CheckOut\" < @today", TripCompletionCounter.HostsSql);
        Assert.Contains("b.\"StartedAt\" IS NOT NULL", TripCompletionCounter.TaxiBookingSql);
    }

    [Fact]
    public void Counter_runs_at_each_completion_site()
    {
        var root = Root();
        Assert.Contains("TripCompletionCounter.CountCompletedTripInTxAsync",
            File.ReadAllText(Path.Combine(root, "RentNearBy.Api", "Handlers", "TripLifecycleHandlers.cs")));
        Assert.Contains("TripCompletionCounter.CountCompletedTripInTxAsync",
            File.ReadAllText(Path.Combine(root, "RentNearBy.Infrastructure", "Repositories", "TripRepository.cs")));
        Assert.Contains("TripCompletionCounter.CountTaxiBookingInTxAsync",
            File.ReadAllText(Path.Combine(root, "RentNearBy.Infrastructure", "Services", "TaxiRequests", "TaxiBookingService.cs")));
    }

    [Fact]
    public void Manual_complete_counts_inside_the_transaction_after_the_status_write()
    {
        var src = File.ReadAllText(Path.Combine(Root(), "RentNearBy.Api", "Handlers", "TripLifecycleHandlers.cs"));
        var status = src.IndexOf("trip.Status = TripStatuses.Completed;", StringComparison.Ordinal);
        var count = src.IndexOf("TripCompletionCounter.CountCompletedTripInTxAsync", StringComparison.Ordinal);
        var commit = src.IndexOf("await unitOfWork.CommitTransactionAsync();", count, StringComparison.Ordinal);
        Assert.True(status > 0 && count > status && commit > count);
    }

    private static List<string> DtosExposing(params string[] names) => typeof(AdminUserDto).Assembly.GetTypes()
        .Where(t => t.Namespace != null && t.Namespace.StartsWith("RentNearBy.Core.DTOs", StringComparison.Ordinal))
        .Where(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Any(p => names.Contains(p.Name)))
        .Select(t => t.Name)
        .Order(StringComparer.Ordinal)
        .ToList();

    [Fact]
    public void Only_the_admin_user_dto_exposes_the_bookings_counter_and_the_raw_columns()
        => Assert.Equal([nameof(AdminUserDto)], DtosExposing("CompletedBookings", "CompletedTripsCount", "CompletedBookingsCount"));

    // Travel levels (spec B): the seeker's completed trips are public as a count + level on the social person and own profile.
    [Fact]
    public void Completed_trips_are_exposed_only_by_the_admin_dto_and_the_social_level_blocks()
    {
        string[] expected =
        [
            nameof(AdminUserDto), nameof(RentNearBy.Core.DTOs.Social.FollowRequestRowDto), nameof(RentNearBy.Core.DTOs.Social.MyProfileDto),
            nameof(RentNearBy.Core.DTOs.Social.PersonRowDto), nameof(RentNearBy.Core.DTOs.Social.SocialPersonDto),
        ];
        Assert.Equal(expected.Order(StringComparer.Ordinal), DtosExposing("CompletedTrips"));
    }
}
