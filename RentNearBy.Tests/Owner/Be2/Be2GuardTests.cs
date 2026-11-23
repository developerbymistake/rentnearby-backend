using System.Text.RegularExpressions;
using RentNearBy.Tests.TaxiRequests;

namespace RentNearBy.Tests.Owner.Be2;

// Contract 8.2 / 11.5 PriceFunctionGuardTests, AU-14a listed-breakdown privacy, 2.0 groups of lane BE2 (source scans, always run).
public class PriceFunctionGuardTests
{
    // Every read that shows a dated stay price, and the file that calls the function there.
    private static readonly (string[] Path, string Why)[] ReadSites =
    [
        (["RentNearBy.Infrastructure", "Services", "Owner", "Be2", "StayDatedPricing.cs"], "T1 list quote, T2 detail"),
        (["RentNearBy.Core", "Models", "Owner", "StaySpecialPriceRules.cs"], "T4 listed total and breakdown"),
        (["RentNearBy.Api", "Handlers", "TripEnquiryHandlers.cs"], "T4 blocked nights at create"),
        (["RentNearBy.Api", "Handlers", "TripEnquiryDtoBuilder.cs"], "T5 listed per night"),
    ];

    [Fact]
    public void Every_dated_price_read_calls_the_one_function()
    {
        foreach (var (path, why) in ReadSites)
            Assert.True(TaxiSource.Read(path).Contains("StaySpecialPriceMath.", StringComparison.Ordinal), $"{string.Join('/', path)} ({why})");
    }

    [Fact]
    public void No_other_file_multiplies_a_price_by_a_percent()
    {
        var percentMath = new Regex(@"\(\s*100\s*[-+]\s*\w*[Pp]ercent", RegexOptions.Compiled);
        var hits = TaxiSource.ProductionFiles()
            .Where(f => Path.GetFileName(f) != "StaySpecialPriceMath.cs" && percentMath.IsMatch(File.ReadAllText(f)))
            .Select(Path.GetFileName)
            .ToList();
        Assert.True(hits.Count == 0, "Price x percent outside StaySpecialPriceMath: " + string.Join(", ", hits));
    }

    [Fact]
    public void The_stay_card_chip_uses_the_rule_percent_and_never_a_label()
    {
        foreach (var file in new[] { TaxiSource.Read("RentNearBy.Infrastructure", "Repositories", "StayListingRepository.cs"),
                     TaxiSource.Read("RentNearBy.Infrastructure", "Repositories", "StayFeedHydrator.cs") })
        {
            Assert.Contains("SpecialPriceDirections.Lower", file);
            Assert.Contains("StaysChipWithinDays", file);
            Assert.DoesNotContain("LabelCopy", file);
        }
    }
}

public class ListedBreakdownPrivacyGuardTests
{
    [Fact]
    public void No_admin_handler_or_traveller_only_dto_names_the_breakdown()
    {
        var hits = TaxiSource.ProductionFiles()
            .Where(f => Path.GetFileName(f).StartsWith("Admin", StringComparison.Ordinal) || f.Contains($"{Path.DirectorySeparatorChar}Hubs{Path.DirectorySeparatorChar}"))
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"\b(ListedStayBreakdownJson|ListedBreakdown)\b"))
            .Select(Path.GetFileName)
            .ToList();
        Assert.True(hits.Count == 0, "The listed breakdown is host-only: " + string.Join(", ", hits));
    }

    [Fact]
    public void The_builder_fills_the_breakdown_only_for_the_host()
    {
        var builder = TaxiSource.Read("RentNearBy.Api", "Handlers", "TripEnquiryDtoBuilder.cs");
        Assert.Contains("ListedBreakdown = ownerViewer && !isTaxi ?", builder);
        Assert.Contains("ListedStayTotal = ownerViewer && !isTaxi ?", builder);
    }
}

public class Be2RouteMapTests
{
    private static readonly string Registration = TaxiSource.Read("RentNearBy.Api", "Extensions", "Owner", "OwnerBe2Registration.cs");
    private static readonly string Prices = TaxiSource.Read("RentNearBy.Api", "Endpoints", "Owner", "StaySpecialPriceEndpoints.cs");
    private static readonly string Festivals = TaxiSource.Read("RentNearBy.Api", "Endpoints", "Owner", "FestivalEndpoints.cs");

    [Fact]
    public void Special_prices_sit_on_the_ungated_stay_owner_group()
    {
        Assert.Contains("MapGroup(\"/api/v1/stays/me\").WithTags(\"Stay owner\").RequireAuthorization(\"CanListStays\").MapStaySpecialPriceEndpoints()", Registration);
        Assert.DoesNotContain("<TabGateFilter>", Registration);
        Assert.DoesNotContain("TabGateFilter", Prices);
        foreach (var route in new[]
                 {
                     "MapGet(\"/special-prices\"", "MapPost(\"/special-prices\"", "MapPut(\"/special-prices/{id:guid}\"",
                     "MapDelete(\"/special-prices/{id:guid}\"", "MapPut(\"/special-prices/{id:guid}/pause\"", "MapPut(\"/special-prices/{id:guid}/live\"",
                 })
            Assert.Equal(1, Regex.Matches(Prices, Regex.Escape(route)).Count);
    }

    [Fact]
    public void Festival_reads_need_a_sign_in_and_admin_routes_are_admin_only()
    {
        Assert.Contains("MapGet(\"\", FestivalHandlers.List).RequireAuthorization()", Festivals);
        var admin = Regex.Matches(Festivals, @"Map(Get|Post|Put|Delete)\(""/festivals[^\r\n]*").Select(m => m.Value).ToList();
        Assert.Equal(5, admin.Count);
        Assert.All(admin, line => Assert.EndsWith(".RequireAuthorization(\"AdminOnly\");", line));
    }

    [Fact]
    public void No_route_of_this_lane_is_a_payment_route()
    {
        var segments = Regex.Matches(Prices + Festivals, @"""(/[^""]*)""").SelectMany(m => m.Groups[1].Value.Split('/', StringSplitOptions.RemoveEmptyEntries));
        foreach (var s in segments)
        {
            Assert.False(s is "pay" || s.StartsWith("pay-", StringComparison.Ordinal), s);
            foreach (var bad in new[] { "advance", "proof", "hold", "upi" })
                Assert.False(s.StartsWith(bad, StringComparison.Ordinal), s);
        }
    }
}
