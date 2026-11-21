using System.Text.RegularExpressions;

namespace RentNearBy.Tests.Owner.Be3;

// Contract R-5: GET /stays/me and GET /taxis/me (and V1 / V2) sit in owner groups with no TabGateFilter, mapped once; every
// other /stays/me and /taxis/me route stays in the gated groups.
public class OwnerRouteMapTests
{
    private static readonly string Owner = Be3Kit.Source("RentNearBy.Api", "Endpoints", "Owner", "PlanVisibleEndpoints.cs");
    private static readonly string Stay = Be3Kit.Source("RentNearBy.Api", "Endpoints", "StayEndpoints.cs");
    private static readonly string Taxi = Be3Kit.Source("RentNearBy.Api", "Endpoints", "TaxiEndpoints.cs");

    [Fact]
    public void The_owner_reads_and_plan_visible_are_ungated_and_need_the_owner_policy()
    {
        Assert.DoesNotContain("AddEndpointFilter", Owner);
        Assert.Contains("MapGroup(\"/api/v1/stays/me\").WithTags(\"Stay owner\").RequireAuthorization(\"CanListStays\")", Owner);
        Assert.Contains("MapGroup(\"/api/v1/taxis/me\").WithTags(\"Taxi driver\").RequireAuthorization(\"CanListTaxis\")", Owner);
        Assert.Contains("stay.MapGet(\"\", StayHandlers.GetMine)", Owner);
        Assert.Contains("taxi.MapGet(\"\", TaxiHandlers.GetMine)", Owner);
        Assert.Contains("stay.MapPut(\"/plan-visible\", PlanVisibleHandlers.SaveStay)", Owner);
        Assert.Contains("taxi.MapPut(\"/plan-visible\", PlanVisibleHandlers.SaveTaxi)", Owner);
    }

    [Fact]
    public void The_gated_groups_no_longer_map_the_read_and_keep_every_write()
    {
        Assert.Equal(0, Regex.Matches(Stay, @"MapGet\(""/me"",").Count);
        Assert.Equal(0, Regex.Matches(Taxi, @"MapGet\(""/me"",").Count);
        Assert.Contains("MapDelete(\"/me\", StayHandlers.DeleteMine)", Stay);
        Assert.Contains("MapDelete(\"/me\", TaxiHandlers.DeleteMine)", Taxi);
    }
}
