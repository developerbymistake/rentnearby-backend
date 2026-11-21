using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Services.Owner.Be3;

namespace RentNearBy.Tests.Owner.Be3;

// A3 (AU-24): each queue row carries old and new value of every changed field and how far the pin moved.
public class AdminPendingEditListTests
{
    [Fact]
    public void Fields_carry_live_and_asked_values_in_the_order_of_the_change()
    {
        var live = StayLiveEditMatrixTests.Live();
        var asked = live with { Name = "Lake View Cottage", RegistrationNumber = "UK-999", Latitude = 29.40m };
        var fields = StayPendingEditService.Fields(live, new StayPendingEdit(StayReviewFields.Diff(live, asked), asked));
        Assert.Equal(
            [("name", "Lake View Stay", "Lake View Cottage"), ("registrationNumber", "UK-123", "UK-999"), ("location", "29.39, 79.45", "29.4, 79.45")],
            fields.Select(f => (f.Field, f.Live, f.Asked)));
    }

    [Fact]
    public void Pin_distance_is_in_metres_and_null_when_the_pin_did_not_move()
    {
        var live = StayLiveEditMatrixTests.Live();
        Assert.Null(StayPendingEditService.PinMovedMeters(live, live));
        var moved = StayPendingEditService.PinMovedMeters(live, live with { Latitude = 29.40m });
        Assert.InRange(moved!.Value, 1100, 1125);
    }

    [Fact]
    public void Queue_routes_are_admin_only_and_keyset_paged()
    {
        var endpoints = Be3Kit.Source("RentNearBy.Api", "Endpoints", "Owner", "AdminStayEditEndpoints.cs");
        Assert.Contains("RequireAuthorization(\"AdminOnly\")", endpoints);
        Assert.Contains("\"/pending-edits\"", endpoints);
        Assert.Contains("\"/{id:guid}/pending-edit/approve\"", endpoints);
        Assert.Contains("\"/{id:guid}/pending-edit/reject\"", endpoints);
        var service = Be3Kit.Source("RentNearBy.Infrastructure", "Services", "Owner", "Be3", "StayPendingEditService.cs");
        Assert.Contains("OrderBy(s => s.PendingEditAt).ThenBy(s => s.Id).Take(PageSize + 1)", service);
        Assert.Contains("s.PendingEditJson != null && !s.IsDeleted", service);
    }
}
