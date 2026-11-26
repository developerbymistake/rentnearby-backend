using System.Text.RegularExpressions;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Tests.TaxiRequests;

namespace RentNearBy.Tests.TaxiAvailability;

// Route guards of SPEC 6 / contract 1.2 scope, the single availability block (1.9), the admin policy and the reason privacy rule.
public class TaxiAvailabilityGuardTests
{
    private static string Endpoints(string file) => TaxiSource.Read("RentNearBy.Api", "Endpoints", file);

    private static IEnumerable<(string File, string Text)> Production()
        => TaxiSource.ProductionFiles().Select(f => (f, File.ReadAllText(f)));

    [Fact]
    public void Bucket_a_is_only_on_put_days_off()
    {
        var hits = Production().SelectMany(p => p.Text.Split('\n').Where(l => l.Contains(".AvailabilityHourLimited()") && !l.Contains("static")))
            .Select(l => l.Trim()).ToList();
        Assert.Equal(new[] { "group.MapPut(\"/days-off\", TaxiDayOffHandlers.Save).AvailabilityHourLimited();" }, hits);
    }

    [Fact]
    public void Bucket_b_is_on_exactly_the_four_calendar_writes()
    {
        var hits = Production().SelectMany(p => p.Text.Split('\n').Where(l => l.Contains(".BookingHourLimited()") && !l.Contains("static")))
            .Select(l => l.Trim()).OrderBy(l => l).ToList();
        Assert.Equal(4, hits.Count);
        Assert.Contains(hits, l => l.StartsWith("group.MapPost(\"/calendar/entries\", TaxiCalendarHandlers.Create)"));
        Assert.Contains(hits, l => l.StartsWith("group.MapPut(\"/calendar/entries/{id:guid}\", TaxiCalendarHandlers.Update)"));
        Assert.Contains(hits, l => l.StartsWith("group.MapDelete(\"/calendar/entries/{id:guid}\", TaxiCalendarHandlers.Delete)"));
        Assert.Contains(hits, l => l.StartsWith("group.MapPut(\"/calendar/entries/{id:guid}/money\", TaxiCalendarHandlers.Money)"));
        Assert.DoesNotContain(hits, l => l.Contains("MapGet") || l.Contains("Reminder"));
    }

    [Fact]
    public void The_switch_and_reads_carry_no_hourly_bucket()
    {
        var calendar = Endpoints(Path.Combine("Owner", "TaxiCalendarEndpoints.cs"));
        Assert.DoesNotMatch(new Regex(@"MapGet\([^\n]*HourLimited"), calendar);
        Assert.DoesNotMatch(new Regex(@"/calendar/reminder[^\n]*HourLimited"), calendar);
        Assert.DoesNotMatch(new Regex(@"/me/availability[^\n]*HourLimited"), Endpoints("TaxiEndpoints.cs"));
        Assert.Contains("group.MapGet(\"/days-off\", TaxiDayOffHandlers.Read);", Endpoints(Path.Combine("Owner", "TaxiDayOffEndpoints.cs")));
    }

    [Fact]
    public void Days_off_routes_sit_on_the_driver_group_with_the_driver_policy()
    {
        var reg = TaxiSource.Read("RentNearBy.Api", "Extensions", "Owner", "OwnerBe1Registration.cs");
        Assert.Contains("app.MapGroup(\"/api/v1/taxis/me\").WithTags(\"Taxi driver\").RequireAuthorization(\"CanListTaxis\").MapTaxiCalendarEndpoints().MapTaxiDayOffEndpoints();", reg);
    }

    [Fact]
    public void Every_popular_route_admin_call_needs_the_admin_policy()
    {
        var lines = Endpoints("AdminTaxiEndpoints.cs").Split('\n').Where(l => l.Contains("popular-routes")).ToList();
        Assert.Equal(5, lines.Count);
        Assert.All(lines, l => Assert.Contains(".RequireAuthorization(\"AdminOnly\")", l));
        Assert.Contains("group.MapGet(\"/popular-routes\", PopularRouteHandlers.Get);", Endpoints("TaxiEndpoints.cs"));
    }

    [Fact]
    public void Nobody_writes_the_day_off_not_exists_outside_the_building_block()
    {
        var allowed = new[] { "TaxiAvailabilitySql.cs" };
        var offenders = Production()
            .Where(p => !allowed.Any(a => p.File.EndsWith(a)))
            .Where(p => p.Text.Contains("NOT EXISTS (SELECT 1 FROM \\\"TaxiDayOffs\\\"") || p.Text.Contains("NOT EXISTS (SELECT 1 FROM \"TaxiDayOffs\""))
            .Select(p => Path.GetFileName(p.File)).ToList();
        Assert.True(offenders.Count == 0, "Use TaxiAvailabilitySql: " + string.Join(", ", offenders));
    }

    [Fact]
    public void The_reason_is_read_only_by_the_driver_service()
    {
        var readers = Production()
            .Where(p => Regex.IsMatch(p.Text, @"d\.\\?""Reason\\?""") && p.Text.Contains("TaxiDayOffs"))
            .Select(p => Path.GetFileName(p.File)).ToList();
        Assert.Equal(new[] { "TaxiDayOffService.cs" }, readers);
    }

    [Fact]
    public void No_log_line_in_the_day_off_code_carries_the_reason()
    {
        foreach (var file in new[] { "TaxiDayOffService.cs", "TaxiDayOffHandlers.cs", "TaxiHourlyLimitFilter.cs" })
        {
            var path = TaxiSource.ProductionFiles().Single(f => f.EndsWith(file));
            foreach (var line in File.ReadAllLines(path).Where(l => l.Contains("Log")))
                Assert.DoesNotContain("reason", line, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData(typeof(TaxiFeedItemDto))]
    [InlineData(typeof(TaxiBrowseDto))]
    [InlineData(typeof(TaxiRouteFeedDto))]
    [InlineData(typeof(PopularRoutePublicDto))]
    [InlineData(typeof(PopularRouteAdminDto))]
    public void Seeker_public_and_admin_dtos_have_no_reason(Type type)
        => Assert.DoesNotContain(type.GetProperties(), p => p.Name.Contains("Reason", StringComparison.Ordinal) || p.Name.Contains("DayOff", StringComparison.Ordinal));

    [Fact]
    public void Popular_routes_have_no_seed_or_automatic_writer()
    {
        var writers = Production()
            .Where(p => p.Text.Contains("INSERT INTO \"PopularRoutes\"") || p.Text.Contains("Set<PopularRoute>()") || p.Text.Contains("PopularRoutes.Add"))
            .Select(p => Path.GetFileName(p.File)).ToList();
        Assert.Equal(new[] { "PopularRouteReader.cs" }, writers);
    }
}
