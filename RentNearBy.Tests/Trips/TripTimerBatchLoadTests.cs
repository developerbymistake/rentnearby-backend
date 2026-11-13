using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.Trips;

// Trip timer passes load enquiries and journeys once per page; the per-trip lists must equal the old per-trip queries.
public class TripTimerBatchLoadTests
{
    private static readonly Guid Owner = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Other = Guid.Parse("00000000-0000-0000-0000-0000000000b2");
    private static readonly DateTime T0 = new(2026, 11, 1, 6, 0, 0, DateTimeKind.Utc);

    private static SavedJourney Journey(Guid tripId, Guid userId, int minutes, bool deleted = false) => new()
    {
        Id = Guid.NewGuid(), TripId = tripId, UserId = userId, UpdatedAt = T0.AddMinutes(minutes), IsDeleted = deleted,
    };

    private static TripEnquiry Enquiry(Guid tripId, string status = TripEnquiryStatuses.Asked) => new()
    {
        Id = Guid.NewGuid(), TripId = tripId, Status = status,
    };

    [Fact]
    public void Rows_of_another_user_on_the_same_trip_are_dropped()
    {
        var trip = Guid.NewGuid();
        var mine = Journey(trip, Owner, 1);
        var rows = TripTimerBatch.Group([], [Journey(trip, Other, 5), mine], [(trip, Owner)]);
        Assert.Equal([mine.Id], rows[trip].Journeys.Select(j => j.Id));
    }

    [Fact]
    public void Journeys_are_updated_desc_and_enquiries_keep_input_order()
    {
        var trip = Guid.NewGuid();
        var old = Journey(trip, Owner, 1);
        var mid = Journey(trip, Owner, 5);
        var fresh = Journey(trip, Owner, 9);
        var e1 = Enquiry(trip);
        var e2 = Enquiry(trip);
        var e3 = Enquiry(trip);
        var rows = TripTimerBatch.Group([e2, e3, e1], [mid, old, fresh], [(trip, Owner)]);
        Assert.Equal([fresh.Id, mid.Id, old.Id], rows[trip].Journeys.Select(j => j.Id));
        Assert.Equal([e2.Id, e3.Id, e1.Id], rows[trip].Enquiries.Select(e => e.Id));
    }

    [Fact]
    public void Ties_keep_the_query_order()
    {
        var trip = Guid.NewGuid();
        var a = Journey(trip, Owner, 3);
        var b = Journey(trip, Owner, 3);
        Assert.Equal([a.Id, b.Id], TripTimerBatch.Group([], [a, b], [(trip, Owner)])[trip].Journeys.Select(j => j.Id));
        Assert.Equal([b.Id, a.Id], TripTimerBatch.Group([], [b, a], [(trip, Owner)])[trip].Journeys.Select(j => j.Id));
    }

    [Fact]
    public void A_trip_with_no_rows_gets_empty_lists_and_rows_stay_on_their_own_trip()
    {
        var busy = Guid.NewGuid();
        var empty = Guid.NewGuid();
        var rows = TripTimerBatch.Group([Enquiry(busy)], [Journey(busy, Owner, 1)], [(busy, Owner), (empty, Owner)]);
        Assert.Empty(rows[empty].Enquiries);
        Assert.Empty(rows[empty].Journeys);
        Assert.Single(rows[busy].Enquiries);
        Assert.Single(rows[busy].Journeys);
    }

    [Fact]
    public void Deleted_journeys_never_appear()
    {
        var trip = Guid.NewGuid();
        var live = Journey(trip, Owner, 1);
        var rows = TripTimerBatch.Group([], [Journey(trip, Owner, 9, deleted: true), live], [(trip, Owner)]);
        Assert.Equal([live.Id], rows[trip].Journeys.Select(j => j.Id));
    }

    [Fact]
    public void Each_trip_uses_its_own_owner()
    {
        var t1 = Guid.NewGuid();
        var t2 = Guid.NewGuid();
        var j1 = Journey(t1, Owner, 1);
        var j2 = Journey(t2, Other, 1);
        var rows = TripTimerBatch.Group([], [j1, j2, Journey(t1, Other, 2), Journey(t2, Owner, 2)], [(t1, Owner), (t2, Other)]);
        Assert.Equal([j1.Id], rows[t1].Journeys.Select(j => j.Id));
        Assert.Equal([j2.Id], rows[t2].Journeys.Select(j => j.Id));
    }

    // N trips in a page: one enquiry query and one journey query, no per-trip query, no completion (all have an open ask).
    [Fact]
    public async Task Auto_complete_page_loads_enquiries_and_journeys_once()
    {
        var trips = Enumerable.Range(0, 5)
            .Select(_ => new Trip { Id = Guid.NewGuid(), OwnerUserId = Owner, Status = TripStatuses.Planned }).ToList();
        var calls = new List<string>();

        var tripRepo = Fake<ITripRepository>.Create((m, args) =>
        {
            calls.Add(m.Name);
            return m.Name switch
            {
                nameof(ITripRepository.GetAutoCompleteCandidatesAsync) =>
                    Task.FromResult<(IReadOnlyList<Trip>, Guid?)>((trips, null)),
                nameof(ITripRepository.TryCompleteAsync) => Task.FromResult(1),
                _ => throw new InvalidOperationException(m.Name),
            };
        });
        var enquiryRepo = Fake<ITripEnquiryRepository>.Create((m, args) =>
        {
            calls.Add(m.Name);
            return m.Name == nameof(ITripEnquiryRepository.GetByTripsAsync)
                ? Task.FromResult<IReadOnlyList<TripEnquiry>>(trips.Select(t => Enquiry(t.Id)).ToList())
                : throw new InvalidOperationException(m.Name);
        });
        var journeyRepo = Fake<ISavedJourneyRepository>.Create((m, args) =>
        {
            calls.Add(m.Name);
            return m.Name == nameof(ISavedJourneyRepository.GetByTripIdsAsync)
                ? Task.FromResult(new List<SavedJourney>())
                : throw new InvalidOperationException(m.Name);
        });
        var uow = Fake<IUnitOfWork>.Create((m, args) => m.Name switch
        {
            "get_Trips" => tripRepo,
            "get_TripEnquiries" => enquiryRepo,
            _ => throw new InvalidOperationException(m.Name),
        });

        var sp = new ServiceCollection().AddSingleton(uow).AddSingleton(journeyRepo).BuildServiceProvider();
        var service = new TripTimerService(null!, null!, NullLogger<TripTimerService>.Instance);
        var pass = typeof(TripTimerService).GetMethod("AutoCompletePassAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var done = await (Task<int>)pass.Invoke(service, [sp, CancellationToken.None])!;

        Assert.Equal(0, done);
        Assert.Equal(1, calls.Count(c => c == nameof(ITripEnquiryRepository.GetByTripsAsync)));
        Assert.Equal(1, calls.Count(c => c == nameof(ISavedJourneyRepository.GetByTripIdsAsync)));
        Assert.DoesNotContain(nameof(ITripEnquiryRepository.GetByTripAsync), calls);
        Assert.DoesNotContain(nameof(ITripRepository.TryCompleteAsync), calls);
    }

    public class Fake<T> : DispatchProxy where T : class
    {
        private Func<MethodInfo, object?[]?, object?> _handler = null!;

        public static T Create(Func<MethodInfo, object?[]?, object?> handler)
        {
            var proxy = DispatchProxy.Create<T, Fake<T>>();
            ((Fake<T>)(object)proxy)._handler = handler;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => _handler(targetMethod!, args);
    }
}
