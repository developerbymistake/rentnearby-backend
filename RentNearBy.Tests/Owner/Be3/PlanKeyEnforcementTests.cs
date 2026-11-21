using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.Models.Owner;

namespace RentNearBy.Tests.Owner.Be3;

// Contract 3.2: the server-side rejection point of the plan keys of lane BE3 (Photos, RoomTypes, Routes). One tagged test per key;
// the rest of the class pins the full-replace rule, the hard ceilings and the two message lines.
public class PlanKeyEnforcementTests
{
    [Fact]
    [PlanKeyTest(PlanFeatureKeys.Photos)]
    public void Photos_ThirdOnFree_Refused()
    {
        var free = Be3Kit.Free(ListingKinds.Stay);
        Assert.Equal(2, free.Photos);
        Assert.True(PlanRowChoice.PhotoUploadAllowed(visible: 1, all: 1, free.Photos));
        Assert.False(PlanRowChoice.PhotoUploadAllowed(visible: 2, all: 2, free.Photos));
        Assert.Equal("Free plan allows 2 photos.", OwnerErrorTypes.LimitMessage(OwnerErrorTypes.PlanPhotoLimit, free.Photos, free.CanUpgrade));
        Assert.Equal(409, OwnerErrorTypes.StatusOf(OwnerErrorTypes.PlanPhotoLimit));

        var plus = Be3Kit.Plus(ListingKinds.Stay);
        Assert.True(PlanRowChoice.PhotoUploadAllowed(7, 7, plus.Photos));
        Assert.False(PlanRowChoice.PhotoUploadAllowed(8, 8, plus.Photos));
        Assert.Equal("You can add up to 8 photos.", OwnerErrorTypes.LimitMessage(OwnerErrorTypes.PlanPhotoLimit, plus.Photos, plus.CanUpgrade));
    }

    [Fact]
    public void Photo_upload_is_refused_at_the_ceiling_of_twelve_rows_even_with_hidden_photos()
    {
        Assert.False(PlanRowChoice.PhotoUploadAllowed(visible: 2, all: OwnerLimits.PhotosCeiling, limit: 8));
        Assert.True(PlanRowChoice.PhotoUploadAllowed(visible: 2, all: 11, limit: 8));
        Assert.Equal(12, StayLimits.MaxPhotos);
    }

    [Fact]
    public void Payment_off_photo_cap_is_the_plus_number_and_never_offers_an_upgrade()
    {
        var off = Be3Kit.Off(ListingKinds.Stay);
        Assert.Equal(8, off.Photos);
        Assert.False(off.CanUpgrade);
        Assert.Equal("You can add up to 8 photos.", OwnerErrorTypes.LimitMessage(OwnerErrorTypes.PlanPhotoLimit, off.Photos, off.CanUpgrade));
    }

    [Fact]
    [PlanKeyTest(PlanFeatureKeys.RoomTypes)]
    public void RoomTypes_ThirdOnFree_Refused()
    {
        var free = Be3Kit.Free(ListingKinds.Stay);
        Assert.Equal(2, free.RoomTypes);
        Assert.False(PlanRowChoice.SaveAllowed(requestCount: 3, free.RoomTypes, visibleNow: 2));
        Assert.True(PlanRowChoice.SaveAllowed(2, free.RoomTypes, 2));
        Assert.True(PlanRowChoice.SaveAllowed(1, free.RoomTypes, 2));
        Assert.Equal("2 room types on Free.", OwnerErrorTypes.LimitMessage(OwnerErrorTypes.PlanRoomTypeLimit, free.RoomTypes, free.CanUpgrade));
        Assert.Equal("You can add up to 6 room types.",
            OwnerErrorTypes.LimitMessage(OwnerErrorTypes.PlanRoomTypeLimit, Be3Kit.Plus(ListingKinds.Stay).RoomTypes, false));
    }

    [Theory]
    [InlineData(6, 2, 6, true)]   // an edit of an over-limit set (after Plus ended, nothing hidden yet) never hits the limit
    [InlineData(5, 2, 6, true)]   // shrinking passes
    [InlineData(7, 2, 6, false)]  // growing is refused
    [InlineData(3, 6, 2, true)]   // under the limit
    public void Full_replace_keeps_or_shrinks_an_over_limit_set_and_refuses_growth(int request, int limit, int visible, bool allowed)
        => Assert.Equal(allowed, PlanRowChoice.SaveAllowed(request, limit, visible));

    [Fact]
    public void Rooms_above_the_ceiling_of_eight_stay_a_validator_400()
    {
        var validator = new UpsertStayRoomsRequestValidator();
        string[] names = ["Standard", "Deluxe", "Premium", "Suite", "Family Room", "Executive", "Dormitory", "Cottage", "Tent"];
        var nine = names.Take(9).Select(n => new StayRoomInput { RoomName = n, PricePerNight = 1000, MaxGuests = 2, Meals = StayMeals.All[0] }).ToList();
        var result = validator.Validate(new UpsertStayRoomsRequest { Rooms = nine });
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(UpsertStayRoomsRequest.Rooms) && e.ErrorMessage.Contains("at most 8"));
        Assert.Equal(8, StayLimits.MaxRooms);
    }

    [Fact]
    [PlanKeyTest(PlanFeatureKeys.Routes)]
    public void Routes_SecondOnFree_Refused()
    {
        var free = Be3Kit.Free(ListingKinds.Taxi);
        Assert.Equal(1, free.Routes);
        Assert.False(PlanRowChoice.SaveAllowed(2, free.Routes, 1));
        Assert.True(PlanRowChoice.SaveAllowed(1, free.Routes, 1));
        Assert.Equal("1 route on Free.", OwnerErrorTypes.LimitMessage(OwnerErrorTypes.TaxiRouteLimit, free.Routes, free.CanUpgrade));
        Assert.Equal(TaxiErrorTypes.TaxiRouteLimit, OwnerErrorTypes.TaxiRouteLimit);
    }

    [Fact]
    public void Routes_SevenKeptWhileOff_AddRefused()
    {
        var off = Be3Kit.Off(ListingKinds.Taxi);
        Assert.Equal(5, off.Routes);
        Assert.True(PlanRowChoice.SaveAllowed(7, off.Routes, visibleNow: 7));
        Assert.True(PlanRowChoice.SaveAllowed(6, off.Routes, visibleNow: 7));
        Assert.False(PlanRowChoice.SaveAllowed(8, off.Routes, visibleNow: 7));
        Assert.Equal("You can add up to 5 routes.", OwnerErrorTypes.LimitMessage(OwnerErrorTypes.TaxiRouteLimit, off.Routes, off.CanUpgrade));
        Assert.Equal(10, TaxiLimits.MaxRoutes);
    }

    [Fact]
    public void Handlers_check_the_plan_key_at_their_rejection_points()
    {
        var stay = Be3Kit.Source("RentNearBy.Api", "Handlers", "StayHandlers.cs");
        Assert.Contains("OwnerHttp.LimitReached(OwnerErrorTypes.PlanPhotoLimit, PlanFeatureKeys.Photos", stay);
        Assert.Contains("PlanRowChoice.PhotoUploadAllowed(counts.Visible, counts.All, limits.Photos)", stay);
        Assert.Contains("OwnerLocks.LockStayAsync(db, stay.Id)", stay);
        Assert.Contains("PlanRowChoice.SaveAllowed(request.Rooms.Count, limits.RoomTypes, stay.Rooms.Count)", stay);
        var taxi = Be3Kit.Source("RentNearBy.Api", "Handlers", "TaxiHandlers.cs");
        Assert.Contains("PlanRowChoice.SaveAllowed(request.Routes.Count, limits.Routes, taxi.Routes.Count)", taxi);
        Assert.Contains("OwnerHttp.LimitReached(TaxiErrorTypes.TaxiRouteLimit, PlanFeatureKeys.Routes", taxi);
    }
}
