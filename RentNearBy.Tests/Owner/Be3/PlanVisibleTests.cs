using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Services.Owner;
using RentNearBy.Infrastructure.Services.Owner.Be3;

namespace RentNearBy.Tests.Owner.Be3;

// Contract 2.4 V1 / V2, AU-9: counts must equal min(limit, rows), ids belong to the listing, cover = first photo id, the 4th change
// of an IST day is refused, the same set writes nothing and does not count, payment OFF is refused.
public class PlanVisibleTests
{
    private static PlanRow R(int index, bool hidden = false) => new(Guid.NewGuid(), hidden, index);

    [Fact]
    public void Counts_must_equal_min_of_limit_and_rows()
    {
        var rows = new[] { R(0), R(1), R(2) };
        var ids = rows.Select(r => r.Id).ToHashSet();
        Assert.True(PlanRowChoice.ValidOwnerChoice(ids, [rows[2].Id, rows[0].Id], 2));
        Assert.False(PlanRowChoice.ValidOwnerChoice(ids, [rows[0].Id], 2));
        Assert.False(PlanRowChoice.ValidOwnerChoice(ids, [rows[0].Id, rows[0].Id], 2));
        Assert.False(PlanRowChoice.ValidOwnerChoice(ids, [rows[0].Id, Guid.NewGuid()], 2));
        Assert.True(PlanRowChoice.ValidOwnerChoice(ids, [.. ids], 5));
        Assert.False(PlanRowChoice.ValidOwnerChoice(ids, [rows[0].Id, rows[1].Id], 5));
    }

    [Fact]
    public void The_first_photo_id_becomes_the_cover_and_hidden_photos_follow()
    {
        var p = new[] { R(0), R(1), R(2), R(3) };
        var targets = PlanRowChoice.OwnerPhotos(p, [p[3].Id, p[1].Id]);
        Assert.Equal((false, 0), targets.Where(t => t.Id == p[3].Id).Select(t => (t.Hidden, t.Index)).Single());
        Assert.Equal((false, 1), targets.Where(t => t.Id == p[1].Id).Select(t => (t.Hidden, t.Index)).Single());
        Assert.All(targets.Where(t => t.Id == p[0].Id || t.Id == p[2].Id), t => Assert.True(t.Hidden));
    }

    [Fact]
    public void Rooms_the_owner_keeps_take_one_to_v_in_their_current_order()
    {
        var a = R(1); var b = R(2); var c = R(3);
        var targets = PlanRowChoice.OwnerIndexed([a, b, c], new HashSet<Guid> { c.Id, a.Id });
        Assert.Equal([(false, 1), (true, 101), (false, 2)], new[] { a, b, c }.Select(r => targets.Single(t => t.Id == r.Id)).Select(t => (t.Hidden, t.Index)));
    }

    [Fact]
    public void The_same_set_changes_nothing()
    {
        var a = R(1); var b = R(101, true);
        var targets = PlanRowChoice.OwnerIndexed([a, b], new HashSet<Guid> { a.Id });
        Assert.Empty(PlanRowChoice.Changed([a, b], targets));
    }

    [Fact]
    public void The_daily_counter_rolls_at_ist_midnight_and_refuses_the_fourth_change()
    {
        var today = new DateOnly(2026, 11, 21);
        Assert.Equal(3, OwnerContext.CountToday(today, 3, today));
        Assert.Equal(0, OwnerContext.CountToday(today.AddDays(-1), 3, today));
        Assert.True(OwnerContext.CountToday(today, 3, today) >= OwnerLimits.PlanVisibleSavesPerDay);
        var service = Be3Kit.Source("RentNearBy.Infrastructure", "Services", "Owner", "Be3", "PlanVisibleService.cs");
        Assert.Contains("if (used >= OwnerLimits.PlanVisibleSavesPerDay)", service);
        // The no-change answer returns before the counter check and writes nothing.
        Assert.True(service.IndexOf("if (photoChanges.Count == 0 && roomChanges.Count == 0)", StringComparison.Ordinal)
            < service.IndexOf("if (used >= OwnerLimits.PlanVisibleSavesPerDay)", StringComparison.Ordinal));
    }

    [Fact]
    public void A_hidden_row_keeps_the_choice_open_even_when_all_rows_fit()
    {
        var a = R(1); var b = R(101, true);
        Assert.True(PlanRowChoice.ChoiceNeeded([a, b], 2));
        Assert.True(PlanRowChoice.ChoiceNeeded([a, R(2), R(3)], 2));
        Assert.False(PlanRowChoice.ChoiceNeeded([a, R(2)], 2));
        Assert.True(PlanRowChoice.ValidOwnerChoice(new HashSet<Guid> { a.Id, b.Id }, [a.Id, b.Id], 2));
        var targets = PlanRowChoice.OwnerIndexed([a, b], new HashSet<Guid> { a.Id, b.Id });
        Assert.All(targets, t => Assert.False(t.Hidden));
    }

    [Fact]
    public void The_same_photos_in_the_same_order_change_nothing_even_with_gaps_in_the_order()
    {
        var a = R(0); var b = R(3); var c = R(5, true);
        Assert.Empty(PlanRowChoice.Changed([a, b, c], PlanRowChoice.OwnerPhotos([a, b, c], [a.Id, b.Id])));
        Assert.NotEmpty(PlanRowChoice.Changed([a, b, c], PlanRowChoice.OwnerPhotos([a, b, c], [b.Id, a.Id])));
    }

    [Fact]
    public void A_refused_route_choice_tells_no_one()
    {
        var handler = Be3Kit.Source("RentNearBy.Api", "Handlers", "Owner", "Be3", "PlanVisibleHandlers.cs");
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(handler, "AfterTaxiCommitAsync").Count);
        var slots = Be3Kit.Source("RentNearBy.Infrastructure", "Services", "PlanSlotService.cs");
        Assert.Contains("if (rows.Changed && rows.HiddenAfter == 0) await planRows.ClearLimitsNoticeAsync(userId);", slots);
    }

    [Fact]
    public void Payment_off_and_nothing_hidden_answer_not_needed()
    {
        var service = Be3Kit.Source("RentNearBy.Infrastructure", "Services", "Owner", "Be3", "PlanVisibleService.cs");
        Assert.Contains("if (!limits.PaymentEnabled) return PlanVisibleOutcome.Fail(OwnerErrorTypes.PlanVisibleNotNeeded);", service);
        Assert.Contains("if (!PlanRowChoice.ChoiceNeeded(photos, locked.Photos) && !PlanRowChoice.ChoiceNeeded(rooms, locked.RoomTypes))", service);
        Assert.Contains("if (!PlanRowChoice.ChoiceNeeded(PlanRows.AsRows(planned), limits.Routes)) return PlanVisibleOutcome.Fail(OwnerErrorTypes.PlanVisibleNotNeeded);", service);
        Assert.Contains("notifier.ClearAsync(NoticeKinds.PlanLimitsChanged", service);
        Assert.Equal(409, OwnerErrorTypes.StatusOf(OwnerErrorTypes.PlanVisibleLimit));
        Assert.Equal(400, OwnerErrorTypes.StatusOf(OwnerErrorTypes.PlanVisibleInvalid));
    }
}

// AU-9: room names are unique across visible and hidden rooms, with the RoomTypeDuplicate type and the room's name in the line.
public class HiddenDuplicateNameTests
{
    [Fact]
    public void Two_rooms_of_one_name_in_the_request_answer_room_type_duplicate()
    {
        var validator = new UpsertStayRoomsRequestValidator();
        var name = RentNearBy.Core.Models.StayRoomNames.All[0];
        var rooms = new List<StayRoomInput>
        {
            new() { RoomName = name, PricePerNight = 1000, MaxGuests = 2, Meals = StayMeals.All[0] },
            new() { RoomName = name, PricePerNight = 1200, MaxGuests = 2, Meals = StayMeals.All[0] },
        };
        var error = Assert.Single(validator.Validate(new UpsertStayRoomsRequest { Rooms = rooms }).Errors);
        Assert.Equal(OwnerErrorTypes.RoomTypeDuplicate, error.ErrorCode);
        Assert.Equal($"You already have a {name} room. Pick another type.", error.ErrorMessage);
    }

    [Fact]
    public void A_hidden_room_blocks_a_new_visible_room_of_its_name()
    {
        var handler = Be3Kit.Source("RentNearBy.Api", "Handlers", "StayHandlers.cs");
        Assert.Contains(".Where(r => r.StayListingId == stay.Id && r.HiddenByPlan).Select(r => r.RoomName)", handler);
        Assert.Contains("OwnerErrorTypes.RoomTypeDuplicateMessage(clash.RoomName), OwnerErrorTypes.RoomTypeDuplicate", handler);
    }
}
