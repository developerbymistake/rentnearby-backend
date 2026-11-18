using System.Text.Json;
using Microsoft.AspNetCore.Http;
using RentNearBy.Api.Handlers;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.DTOs.TaxiRequests;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Services.TaxiRequests;

namespace RentNearBy.Tests.TaxiRequests;

public class TripPlanLockTests
{
    private static readonly Guid RequestId = Guid.Parse("3f9a6c2d-1b7e-4f0a-8c55-2e7d9b4a6101");

    private static TripPlan Plan()
    {
        var plan = new TripPlan
        {
            Legs =
            [
                new TripCityLeg { CityKey = 210000001, CityName = "Nainital", Latitude = 29.39, Longitude = 79.45 },
                new TripCityLeg { CityKey = 210000002, CityName = "Ranikhet", Latitude = 29.64, Longitude = 79.43 },
            ],
            Settings = new TripSettings { StartDate = new DateOnly(2026, 12, 18), Adults = 2, Kids = 1 },
            Days =
            [
                Day(1, 1, "a", "b"),
                Day(2, 1, "c"),
                Day(3, 2, "d", "e"),
            ],
        };
        plan.Normalize();
        return plan;
    }

    private static TripDay Day(int day, int leg, params string[] spots) => new()
    {
        Day = day,
        Leg = leg,
        Spots = spots.Select(id => new TripSpot { Id = id, Name = id.ToUpperInvariant(), Latitude = 29.4, Longitude = 79.4, VisitMinutes = 60 }).ToList(),
    };

    private static TripPlan Copy(TripPlan plan)
    {
        var copy = JsonSerializer.Deserialize<TripPlan>(JsonSerializer.Serialize(plan, TripJson.Options), TripJson.Options)!;
        copy.Normalize();
        return copy;
    }

    private static bool Structural(Action<TripPlan> edit)
    {
        var stored = Plan();
        var incoming = Copy(stored);
        edit(incoming);
        incoming.Normalize();
        return TripPlanLock.IsStructuralChange(stored, incoming);
    }

    [Fact]
    public void Same_plan_is_not_structural() => Assert.False(Structural(_ => { }));

    [Fact]
    public void Moving_a_spot_is_structural()
        => Assert.True(Structural(p =>
        {
            var spot = p.Days[0].Spots[1];
            p.Days[0].Spots.RemoveAt(1);
            p.Days[1].Spots.Add(spot);
        }));

    [Fact]
    public void Reordering_spots_is_structural() => Assert.True(Structural(p => p.Days[0].Spots.Reverse()));

    [Fact]
    public void Removing_or_adding_a_spot_is_structural()
    {
        Assert.True(Structural(p => p.Days[2].Spots.RemoveAt(0)));
        Assert.True(Structural(p => p.Days[1].Spots.Add(new TripSpot { Id = "z", Name = "Z" })));
    }

    [Fact]
    public void Adding_a_day_is_structural() => Assert.True(Structural(p => p.Days.Add(Day(4, 2, "f"))));

    [Fact]
    public void Removing_a_day_or_moving_it_to_another_leg_or_making_it_a_rest_day_is_structural()
    {
        Assert.True(Structural(p => p.Days.RemoveAt(2)));
        Assert.True(Structural(p => p.Days[1].Leg = 2));
        Assert.True(Structural(p => { p.Days[1].RestDay = true; p.Days[1].Spots.Clear(); }));
        Assert.True(Structural(p => p.Days[1].RestDay = true));
    }

    [Fact]
    public void Changing_or_reordering_cities_is_structural()
    {
        Assert.True(Structural(p => p.Legs!.Reverse()));
        Assert.True(Structural(p => p.Legs![1].CityKey = 210000009));
        Assert.True(Structural(p => p.Legs!.Add(new TripCityLeg { CityKey = 210000003, CityName = "Almora" })));
    }

    [Fact]
    public void Changing_the_start_date_is_structural()
    {
        Assert.True(Structural(p => p.Settings.StartDate = new DateOnly(2026, 12, 19)));
        Assert.True(Structural(p => p.Settings.StartDate = null));
    }

    [Fact]
    public void Changing_people_is_structural()
    {
        Assert.True(Structural(p => p.Settings.Adults = 3));
        Assert.True(Structural(p => p.Settings.Kids = 0));
        Assert.True(Structural(p => p.Settings.Kids = null));
    }

    [Fact]
    public void Changing_the_taxi_mark_is_structural()
    {
        Assert.True(Structural(p => p.TaxiMark = new TripRowMark { Status = TripRowMarkStatuses.Own }));
        var stored = Plan();
        stored.TaxiMark = new TripRowMark { Status = TripRowMarkStatuses.Own };
        var skipped = Copy(stored);
        skipped.TaxiMark = new TripRowMark { Status = TripRowMarkStatuses.Skipped };
        Assert.True(TripPlanLock.IsStructuralChange(stored, skipped));
        var cleared = Copy(stored);
        cleared.TaxiMark = null;
        Assert.True(TripPlanLock.IsStructuralChange(stored, cleared));
    }

    [Fact]
    public void Name_guide_and_step_progress_are_not_structural()
    {
        Assert.False(Structural(p =>
        {
            p.Guide = new TripGuide { Step = "stay:1", Choices = new() { ["stay:1"] = TripGuideChoices.Find }, Overview = true, NotNow = ["comingBack"] };
            p.Steps = new TripSteps
            {
                StartFrom = new TripStartFrom { Name = "Delhi", Lat = 28.6, Lon = 77.2 },
                GettingThere = new TripStep { Status = TripStepStatuses.Own },
                ComingBack = new TripStep { Status = TripStepStatuses.Skipped, LeaveOn = TripLeaveOn.LastDayEvening },
            };
            p.Legs![0].StayMark = new TripRowMark { Status = TripRowMarkStatuses.Own };
            p.Legs![1].TravelMark = new TripRowMark { Status = TripRowMarkStatuses.Skipped };
            p.DismissedEnquiryIds = [Guid.NewGuid()];
            p.Settings.DayLimitMinutes = TripPace.Normal;
            p.Days[0].Spots[0].Name = "Renamed label";
        }));
    }

    [Fact]
    public void Message_matches_the_contract_for_both_reasons()
    {
        Assert.Equal("Plan is locked while your taxi request is active. Cancel the request to edit.",
            TripPlanLock.MessageFor(PlanLockReasons.TaxiRequestActive));
        Assert.Equal("Plan is locked while your booking is active. Cancel the booking to edit.",
            TripPlanLock.MessageFor(PlanLockReasons.TaxiBookingActive));
        Assert.Equal("Cancel the taxi request first.", TripPlanLock.DeleteMessage);
    }

    [Fact]
    public void Locked_structural_refusal_equals_the_contract_sample()
    {
        var stored = Plan();
        var incoming = Copy(stored);
        incoming.Days[0].Spots.Reverse();

        var refusal = TripPlanLock.UpdateRefusal(PlanLockReasons.TaxiRequestActive, RequestId, stored, incoming);

        Assert.NotNull(refusal);
        using var sample = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TaxiRequests", "Samples", "error_plan_locked.json")));
        var root = sample.RootElement;
        Assert.Equal(root.GetProperty("code").GetInt32(), refusal.StatusCode);
        Assert.Equal(root.GetProperty("error").GetProperty("type").GetString(), refusal.ErrorType);
        Assert.Equal(root.GetProperty("error").GetProperty("message").GetString(), refusal.Message);
        Assert.Equal(root.GetProperty("data").GetProperty("reason").GetString(), refusal.Data!.Reason);
        Assert.Equal(root.GetProperty("data").GetProperty("requestId").GetGuid(), refusal.Data.RequestId);

        var result = TripHandlers.LockRefusal(refusal);
        Assert.Equal(409, ((IStatusCodeHttpResult)result).StatusCode);
        var body = Assert.IsType<ApiResponse<object>>(((IValueHttpResult)result).Value);
        Assert.Equal(root.GetProperty("status").GetString(), body.Status);
        Assert.Equal(root.GetProperty("code").GetInt32(), body.Code);
        Assert.Equal(root.GetProperty("error").GetProperty("type").GetString(), body.Error!.Type);
        Assert.Equal(root.GetProperty("error").GetProperty("message").GetString(), body.Error.Message);
        var data = Assert.IsType<PlanLockedData>(body.Data);
        Assert.Equal(PlanLockReasons.TaxiRequestActive, data.Reason);
        Assert.Equal(RequestId, data.RequestId);
    }

    [Fact]
    public void Unlocked_or_non_structural_save_is_not_refused()
    {
        var stored = Plan();
        var moved = Copy(stored);
        moved.Days[0].Spots.Reverse();
        var named = Copy(stored);
        named.Guide = new TripGuide { Overview = true };

        Assert.Null(TripPlanLock.UpdateRefusal(null, null, stored, moved));
        Assert.Null(TripPlanLock.UpdateRefusal(PlanLockReasons.TaxiBookingActive, RequestId, stored, named));
        Assert.Equal(TripPlanLock.MessageFor(PlanLockReasons.TaxiBookingActive),
            TripPlanLock.UpdateRefusal(PlanLockReasons.TaxiBookingActive, RequestId, stored, moved)!.Message);
    }

    [Fact]
    public void Delete_is_refused_only_while_locked()
    {
        Assert.Null(TripPlanLock.DeleteRefusal(null, null));
        var refusal = TripPlanLock.DeleteRefusal(PlanLockReasons.TaxiBookingActive, RequestId);
        Assert.NotNull(refusal);
        Assert.Equal(409, refusal.StatusCode);
        Assert.Equal(TaxiRequestErrorTypes.PlanLocked, refusal.ErrorType);
        Assert.Equal("Cancel the taxi request first.", refusal.Message);
        Assert.Equal(PlanLockReasons.TaxiBookingActive, refusal.Data!.Reason);
        Assert.Equal(RequestId, refusal.Data.RequestId);
    }

    [Fact]
    public void Lock_reader_sql_repeats_the_active_index_filter()
    {
        Assert.Contains("\"TripId\" = @tripId", TripPlanLockReader.ActiveRequestSql);
        Assert.Contains($"\"Status\" IN ({RequestStates.ActiveSqlList})", TripPlanLockReader.ActiveRequestSql);
        Assert.Contains("FROM \"TaxiRequests\"", TripPlanLockReader.ActiveRequestSql);
    }

    [Fact]
    public void Plan_locked_data_type_is_the_contract_dto() => Assert.IsType<PlanLockedData>(
        TripPlanLock.DeleteRefusal(PlanLockReasons.TaxiRequestActive, RequestId)!.Data);
}
