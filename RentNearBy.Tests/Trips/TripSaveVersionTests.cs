using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Api.Handlers;
using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Interfaces;

namespace RentNearBy.Tests.Trips;

// G2: PUT /trips/{id} refuses a save built on an older copy of the trip; old apps (no base) save as before.
public class TripSaveVersionTests
{
    private static readonly DateTime Stored = new DateTime(2026, 11, 19, 10, 0, 0, DateTimeKind.Utc).AddTicks(1234567);

    [Fact]
    public void No_base_is_never_stale()
        => Assert.False(TripSaveVersion.IsStale(null, Stored));

    [Fact]
    public void Same_instant_is_not_stale_even_when_one_side_lost_the_seventh_digit()
    {
        Assert.False(TripSaveVersion.IsStale(Stored, Stored));
        Assert.False(TripSaveVersion.IsStale(Stored.AddTicks(-7), Stored));
        Assert.False(TripSaveVersion.IsStale(DateTime.SpecifyKind(Stored, DateTimeKind.Unspecified), Stored));
        Assert.False(TripSaveVersion.IsStale(Stored.ToLocalTime(), Stored));
    }

    [Fact]
    public void Another_save_in_between_is_stale()
    {
        Assert.True(TripSaveVersion.IsStale(Stored.AddMicroseconds(-1), Stored));
        Assert.True(TripSaveVersion.IsStale(Stored.AddSeconds(-30), Stored));
    }

    [Fact]
    public void Base_the_app_echoes_binds_and_matches_the_stored_row()
    {
        var json = """{"plan":{"version":6},"baseUpdatedAt":"2026-11-19T10:00:00.123456Z"}""";
        var request = JsonSerializer.Deserialize<SaveTripRequest>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(DateTimeKind.Utc, request.BaseUpdatedAt!.Value.Kind);
        Assert.False(TripSaveVersion.IsStale(request.BaseUpdatedAt, Stored));

        var old = JsonSerializer.Deserialize<SaveTripRequest>("""{"plan":{"version":6}}""", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Null(old.BaseUpdatedAt);
    }

    [Fact]
    public async Task Update_with_an_older_base_answers_409_TripChanged_and_writes_nothing()
    {
        var (trip, owner) = StoredTrip();
        var result = await UpdateAsync(trip, owner, trip.UpdatedAt.AddSeconds(-5));

        Assert.Equal(StatusCodes.Status409Conflict, ((IStatusCodeHttpResult)result).StatusCode);
        var body = (ApiResponse<object>)((IValueHttpResult)result).Value!;
        Assert.Equal(TripErrorTypes.TripChanged, body.Error!.Type);
        Assert.Equal(TripSaveVersion.ChangedMessage, body.Error.Message);
        Assert.Equal(Stored, trip.UpdatedAt);
    }

    // Past the version check the handler reads the plan lock from the database; this harness has none, so it stops exactly there.
    [Fact]
    public async Task Update_with_the_loaded_base_or_no_base_goes_on_to_save()
    {
        var (trip, owner) = StoredTrip();
        var loaded = await Assert.ThrowsAnyAsync<Exception>(() => UpdateAsync(trip, owner, trip.UpdatedAt.AddTicks(-3)));
        Assert.Contains("TripPlanLockReader", loaded.StackTrace);
        var none = await Assert.ThrowsAnyAsync<Exception>(() => UpdateAsync(trip, owner, null));
        Assert.Contains("TripPlanLockReader", none.StackTrace);
    }

    private static (Trip Trip, Guid Owner) StoredTrip()
    {
        var owner = Guid.NewGuid();
        var trip = new Trip
        {
            Id = Guid.NewGuid(), OwnerUserId = owner, CityName = "Nainital", Status = TripStatuses.Planned,
            PlanJson = JsonSerializer.Serialize(Plan(), TripJson.Options), UpdatedAt = Stored,
        };
        return (trip, owner);
    }

    private static TripPlan Plan() => new()
    {
        CityKey = 502141,
        Legs = [new TripCityLeg { Index = 1, CityKey = 502141, CityName = "Nainital", Latitude = 29.38, Longitude = 79.45 }],
        Days = [new TripDay { Day = 1, Spots = [new TripSpot { Id = "a", Name = "a", Latitude = 29.39, Longitude = 79.45, Category = "attraction", VisitMinutes = 60 }] }],
    };

    private static Task<IResult> UpdateAsync(Trip trip, Guid owner, DateTime? baseUpdatedAt)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, owner.ToString())], "test"));
        var uow = DispatchProxy.Create<IUnitOfWork, UowProxy>();
        ((UowProxy)(object)uow).Trips = TripsProxy.For(trip);
        return TripHandlers.Update(trip.Id, new SaveTripRequest { Plan = Plan(), BaseUpdatedAt = baseUpdatedAt }, new SaveTripRequestValidator(),
            principal, uow, null!, null!, null!, NullLoggerFactory.Instance, null!, null!);
    }

    public class UowProxy : DispatchProxy
    {
        public ITripRepository? Trips;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name == "get_Trips" ? Trips : throw new InvalidOperationException($"{targetMethod?.Name} is not expected");
    }

    public class TripsProxy : DispatchProxy
    {
        private Trip? _trip;

        public static ITripRepository For(Trip trip)
        {
            var p = DispatchProxy.Create<ITripRepository, TripsProxy>();
            ((TripsProxy)(object)p)._trip = trip;
            return p;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name == nameof(ITripRepository.GetByIdForOwnerAsync)
                ? Task.FromResult(_trip!.Id == (Guid)args![0]! && _trip.OwnerUserId == (Guid)args[1]! ? _trip : null)
                : throw new InvalidOperationException($"{targetMethod?.Name} is not expected");
    }
}
