using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RentNearBy.Api.Handlers;
using RentNearBy.Api.Services;
using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Repositories;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Tests.Data;

namespace RentNearBy.Tests.Integration.Journeys;

// One traveller journey on the real database: hand-inserted rows, real handlers, fakes only for push and chat.
// Every handler call gets its own context, like a request scope; cleanup by id in DisposeAsync.
public sealed class JourneyHarness : IAsyncDisposable
{
    private readonly TestDatabase _db;
    private readonly List<Guid> _users = [];
    private readonly List<Guid> _stays = [];
    private readonly List<Guid> _taxis = [];
    private readonly List<Guid> _trips = [];
    private readonly List<Guid> _stayTypes = [];
    private readonly List<Guid> _registrationTypes = [];

    public JourneyHarness(TestDatabase db)
    {
        _db = db;
        Cache = new MemoryCache(new MemoryCacheOptions());
        // No tab row can switch the stay or taxi vertical off in these journeys.
        Cache.Set(AppTabCache.Key, new List<AppTabDto>());
        Services = new ServiceCollection().AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance).BuildServiceProvider();
    }

    public RecordingTripNotifier Notifier { get; } = new();
    public RecordingTripChatService Chat { get; } = new();
    public IRateLimitService RateLimit { get; } = new InMemoryRateLimitService();
    public IMemoryCache Cache { get; }
    public ILoggerFactory Loggers => NullLoggerFactory.Instance;
    public IServiceProvider Services { get; }
    public IOptions<TripExecutionOptions> ExecutionOptions { get; } = Options.Create(new TripExecutionOptions());

    public Guid StayTypeId { get; private set; }
    public Guid RegistrationTypeId { get; private set; }

    public DateOnly Start { get; } = TripExecutionRules.TodayIst(DateTime.UtcNow).AddDays(10);

    public sealed class Scope : IAsyncDisposable
    {
        public Scope(ApplicationDbContext db, JourneyHarness h)
        {
            Db = db;
            Uow = new UnitOfWork(db);
            Conversations = new ConversationFactory(Uow, h.RateLimit, db, h.Cache);
            Journeys = new SavedJourneyRepository(db);
            Closure = new TripEnquiryClosure(db, Uow, h.Chat, h.Notifier, NullLogger<TripEnquiryClosure>.Instance);
            Http = new DefaultHttpContext { RequestServices = h.Services };
        }

        public ApplicationDbContext Db { get; }
        public UnitOfWork Uow { get; }
        public IConversationFactory Conversations { get; }
        public ISavedJourneyRepository Journeys { get; }
        public ITripEnquiryClosure Closure { get; }
        public HttpContext Http { get; }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    public Scope NewScope() => new(_db.CreateContext(), this);

    public static ClaimsPrincipal Principal(Guid userId)
        => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "journey"));

    public static int StatusOf(IResult result) => ((IStatusCodeHttpResult)result).StatusCode ?? 200;

    public static T DataOf<T>(IResult result)
    {
        var body = ((IValueHttpResult)result).Value as ApiResponse<T>
            ?? throw new InvalidOperationException($"Not an ApiResponse<{typeof(T).Name}> (status {StatusOf(result)})");
        return body.Data ?? throw new InvalidOperationException("The response has no data");
    }

    // ---------- rows ----------

    public async Task EnsureLookupsAsync()
    {
        if (StayTypeId != Guid.Empty) return;
        var tag = Guid.NewGuid().ToString("N")[..8];
        await using var db = _db.CreateContext();
        var type = new StayType { Id = Guid.NewGuid(), Name = $"Journey stay {tag}", SortOrder = 999, CreatedAt = DateTime.UtcNow };
        var registration = new StayRegistrationType
        {
            Id = Guid.NewGuid(), Code = $"jt{tag}", Name = $"Journey registration {tag}", SortOrder = 999,
            IsActive = true, CreatedAt = DateTime.UtcNow,
        };
        db.StayTypes.Add(type);
        db.StayRegistrationTypes.Add(registration);
        await db.SaveChangesAsync();
        StayTypeId = type.Id;
        RegistrationTypeId = registration.Id;
        _stayTypes.Add(type.Id);
        _registrationTypes.Add(registration.Id);
    }

    public async Task<Guid> AddUserAsync(Guid roleId, string name)
    {
        var id = Guid.NewGuid();
        var phone = "9" + Random.Shared.NextInt64(100_000_000, 999_999_999);
        await using var db = _db.CreateContext();
        db.Users.Add(new User
        {
            Id = id, PhoneNumber = phone, Name = name, IsActive = true, IsPhoneVerified = true, RoleId = roleId,
            CreatedAt = DateTime.UtcNow.AddDays(-30), UpdatedAt = DateTime.UtcNow.AddDays(-30),
        });
        await db.SaveChangesAsync();
        _users.Add(id);
        return id;
    }

    public Task<Guid> AddSeekerAsync(string name = "Asha") => AddUserAsync(UserRoles.SeekerId, name);
    public Task<Guid> AddStayOwnerAsync(string name = "Host") => AddUserAsync(UserRoles.StayOwnerId, name);
    public Task<Guid> AddTaxiDriverAsync(string name = "Driver") => AddUserAsync(UserRoles.TaxiDriverId, name);

    public async Task<Guid> AddStayAsync(Guid ownerId, string name = "Lake View Stay")
    {
        await EnsureLookupsAsync();
        var id = Guid.NewGuid();
        await using var db = _db.CreateContext();
        db.StayListings.Add(new StayListing
        {
            Id = id, UserId = ownerId, Name = name, StayTypeId = StayTypeId,
            RegistrationTypeId = RegistrationTypeId, RegistrationTypeName = "Journey registration", RegistrationNumber = "JT-1",
            Latitude = 29.3919m, Longitude = 79.4542m, Status = StayStatuses.Approved, EverApproved = true,
            AcceptingGuests = true, UpiId = null, MinPricePerNight = 1500,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        _stays.Add(id);
        return id;
    }

    public async Task<Guid> AddTaxiAsync(Guid driverId)
    {
        var id = Guid.NewGuid();
        await using var db = _db.CreateContext();
        db.TaxiListings.Add(new TaxiListing
        {
            Id = id, UserId = driverId, DrivingLicenceNumberEnc = "journey-test", DateOfBirth = new DateOnly(1985, 1, 1),
            Latitude = 29.3919m, Longitude = 79.4542m, ContactPhone = "9000000001",
            Status = TaxiStatuses.Approved, EverApproved = true, IsAvailable = true, LocalDayRate = 3000, LocalDayKm = 250,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        _taxis.Add(id);
        return id;
    }

    // Legs in order; Days is the number of days of that leg. One place a day; dates from Start; Normalize makes it v6.
    public TripPlan BuildPlan(params (string City, long Key, int Days)[] legs)
    {
        var plan = new TripPlan
        {
            Legs = legs.Select((l, i) => new TripCityLeg
            {
                Index = i + 1, CityKey = l.Key, CityName = l.City,
                Latitude = 29.39 + i * 0.1, Longitude = 79.45 + i * 0.1,
            }).ToList(),
            Settings = new TripSettings { DayLimitMinutes = TripPace.Relaxed, StartDate = Start, Adults = 2, Kids = 0 },
        };
        var day = 1;
        for (var i = 0; i < legs.Length; i++)
            for (var n = 0; n < legs[i].Days; n++, day++)
                plan.Days.Add(new TripDay
                {
                    Day = day, Leg = i + 1,
                    Spots =
                    [
                        new TripSpot
                        {
                            Id = $"spot-{day}", Name = $"Place {day}", Latitude = 29.39 + i * 0.1, Longitude = 79.45 + i * 0.1,
                            Category = "attraction", VisitMinutes = 60,
                        },
                    ],
                });
        plan.Normalize();
        return plan;
    }

    public async Task<Guid> AddTripAsync(Guid seekerId, TripPlan plan)
    {
        var id = Guid.NewGuid();
        var first = plan.Legs![0];
        await using var db = _db.CreateContext();
        db.Trips.Add(new Trip
        {
            Id = id, OwnerUserId = seekerId, CityKey = first.CityKey, CityName = first.CityName,
            CityLatitude = first.Latitude, CityLongitude = first.Longitude, Title = first.CityName,
            Status = TripStatuses.Draft, PlanJson = JsonSerializer.Serialize(plan, TripJson.Options),
            DayCount = plan.Days.Count, SpotCount = plan.Days.Sum(d => d.Spots.Count),
            StartDate = plan.Settings.StartDate, EndDate = plan.EndDate,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        _trips.Add(id);
        return id;
    }

    // ---------- reads (fresh context, nothing tracked) ----------

    public async Task<(Trip Trip, TripPlan Plan, IReadOnlyList<TripEnquiry> Enquiries)> LoadAsync(Guid tripId)
    {
        await using var db = _db.CreateContext();
        var trip = await db.Trips.AsNoTracking().FirstAsync(t => t.Id == tripId);
        var enquiries = await new UnitOfWork(db).TripEnquiries.GetByTripAsync(tripId);
        return (trip, TripPlanReader.Read(trip), enquiries);
    }

    public async Task<List<TripEasyRow>> RowsAsync(Guid tripId)
    {
        var (trip, plan, enquiries) = await LoadAsync(tripId);
        return TripEasyRows.Build(trip, plan, enquiries, null);
    }

    public async Task<TripProgress> ProgressAsync(Guid tripId)
    {
        var (trip, plan, enquiries) = await LoadAsync(tripId);
        return TripEasyRows.Compute(trip, plan, enquiries, null);
    }

    public static TripEasyRow RowOf(IEnumerable<TripEasyRow> rows, string key) => rows.First(r => r.Key.Id == key);

    // ---------- handler calls ----------

    public async Task<IResult> AskAsync(Guid tripId, Guid callerId, CreateTripEnquiryRequest request)
    {
        await using var s = NewScope();
        return await TripEnquiryHandlers.Create(tripId, request, new CreateTripEnquiryRequestValidator(), Principal(callerId),
            s.Uow, s.Db, s.Conversations, Chat, Notifier, RateLimit, Cache, Loggers, s.Http, new RentNearBy.Infrastructure.Repositories.OutboxWriter(s.Db));
    }

    public async Task<IResult> TaxiBookAsync(Guid tripId, Guid callerId, CreateTaxiBookingRequest request)
    {
        await using var s = NewScope();
        return await TripEnquiryHandlers.CreateTaxiBooking(tripId, request, new CreateTaxiBookingRequestValidator(), Principal(callerId),
            s.Uow, s.Db, s.Journeys, Chat, Notifier, RateLimit, Loggers, s.Http, new RentNearBy.Infrastructure.Repositories.OutboxWriter(s.Db));
    }

    public async Task<IResult> CancelAsync(Guid enquiryId, Guid callerId, string? reason = null)
    {
        await using var s = NewScope();
        return await TripEnquiryHandlers.Cancel(enquiryId, new CancelTripEnquiryRequest { Reason = reason },
            new CancelTripEnquiryRequestValidator(), Principal(callerId), s.Uow, s.Db, Chat, Notifier, RateLimit, Loggers, s.Http);
    }

    public async Task<IResult> SendOfferAsync(Guid enquiryId, Guid ownerId, SendTripOfferRequest request)
    {
        await using var s = NewScope();
        return await TripOwnerHandlers.SendOffer(enquiryId, request, new SendTripOfferRequestValidator(), Principal(ownerId),
            s.Uow, s.Db, RateLimit, Chat, Notifier, s.Http);
    }

    public async Task<IResult> AcceptOfferAsync(Guid enquiryId, Guid offerId, Guid callerId)
    {
        await using var s = NewScope();
        return await TripEnquiryHandlers.AcceptOffer(enquiryId, offerId, new AcceptTripOfferRequest(),
            new AcceptTripOfferRequestValidator(), Principal(callerId), s.Uow, s.Db, Chat, Notifier, Loggers);
    }

    // Saves the stored plan after `edit` changed it (marks, steps); the status stays as it is.
    public async Task<IResult> UpdatePlanAsync(Guid tripId, Guid callerId, Action<TripPlan> edit)
    {
        var (_, plan, _) = await LoadAsync(tripId);
        edit(plan);
        await using var s = NewScope();
        return await TripHandlers.Update(tripId, new SaveTripRequest { Plan = plan }, new SaveTripRequestValidator(), Principal(callerId),
            s.Uow, s.Db, Chat, Notifier, Loggers, s.Journeys, new NoCatalog());
    }

    public async Task<IResult> CompleteAsync(Guid tripId, Guid callerId)
    {
        await using var s = NewScope();
        return await TripLifecycleHandlers.Complete(tripId, Principal(callerId), s.Uow, s.Db, Chat, Loggers, s.Journeys, Notifier);
    }

    public async Task<IResult> SummaryAsync(Guid tripId, Guid callerId)
    {
        await using var s = NewScope();
        return await TripLifecycleHandlers.Summary(tripId, Principal(callerId), s.Uow, s.Journeys);
    }

    public async Task<IResult> DeleteTripAsync(Guid tripId, Guid callerId)
    {
        await using var s = NewScope();
        return await TripHandlers.Delete(tripId, Principal(callerId), s.Uow, s.Closure, s.Journeys);
    }

    // ---------- cleanup ----------

    public async ValueTask DisposeAsync()
    {
        await using var db = _db.CreateContext();
        var ids = _users.ToList();
        var trips = _trips.ToList();
        var stays = _stays.ToList();
        var taxis = _taxis.ToList();
        var stayTypes = _stayTypes.ToList();
        var registrationTypes = _registrationTypes.ToList();
        await db.TripEnquiries.Where(e => e.TripId != null && trips.Contains(e.TripId.Value)).ExecuteDeleteAsync();
        await db.Trips.Where(t => trips.Contains(t.Id)).ExecuteDeleteAsync();
        await db.Conversations.Where(c => ids.Contains(c.RenterId) || ids.Contains(c.OwnerId)).ExecuteDeleteAsync();
        await db.StayListings.Where(l => stays.Contains(l.Id)).ExecuteDeleteAsync();
        await db.TaxiListings.Where(l => taxis.Contains(l.Id)).ExecuteDeleteAsync();
        await db.Users.Where(u => ids.Contains(u.Id)).ExecuteDeleteAsync();
        await db.StayTypes.Where(t => stayTypes.Contains(t.Id)).ExecuteDeleteAsync();
        await db.StayRegistrationTypes.Where(t => registrationTypes.Contains(t.Id)).ExecuteDeleteAsync();
        if (Cache is IDisposable disposable) disposable.Dispose();
    }

    // A journey never reaches Atlas: the plans it saves keep the legs the trip row already holds.
    private sealed class NoCatalog : ITripCityCatalog
    {
        public Task<TripCityCatalogSnapshot?> GetAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(bool Available, TripCityInfo? City)> FindAsync(long cityKey, CancellationToken ct = default) => throw new NotSupportedException();
        public Task InvalidateAsync() => throw new NotSupportedException();
    }
}

public sealed class RecordingTripNotifier : ITripNotifier
{
    public List<NotificationEvent> Events { get; } = [];
    public List<string> Keys { get; } = [];

    public Task SendAsync(NotificationEvent n)
    {
        Events.Add(n);
        return Task.CompletedTask;
    }

    public Task PublishLiveAsync(IEnumerable<NotificationEvent> events) => Task.CompletedTask;

    public Task<bool> SendOnceAsync(NotificationEvent n, string dedupeKey, Guid? tripId, Guid? enquiryId)
    {
        Events.Add(n);
        Keys.Add(dedupeKey);
        return Task.FromResult(true);
    }

    public Task<TripSendOutcome> TrySendOnceAsync(NotificationEvent n, string dedupeKey, Guid? tripId, Guid? enquiryId)
    {
        Events.Add(n);
        Keys.Add(dedupeKey);
        return Task.FromResult(TripSendOutcome.Sent);
    }
}

// Cards and system lines are answered in memory: nothing is written to Messages.
public sealed class RecordingTripChatService : ITripChatService
{
    public List<string> Posted { get; } = [];

    public Task<TripChatPost> PostAsync(Conversation c, Guid senderId, string type, object payload, string preview)
    {
        Posted.Add(type);
        return Task.FromResult(New(c, senderId, type));
    }

    public Task<TripChatPost> PostSystemAsync(Conversation c, Guid actorUserId, string kind, string text)
    {
        Posted.Add(kind);
        return Task.FromResult(New(c, actorUserId, "system"));
    }

    public Task<TripChatPost?> RewriteStatusAsync(Guid messageId, string status) => Task.FromResult<TripChatPost?>(null);

    public Task<Guid?> FindCardMessageIdAsync(Guid conversationId, string type, Guid enquiryId) => Task.FromResult<Guid?>(null);

    public Task AfterCommitAsync(IEnumerable<TripChatPost> posts) => Task.CompletedTask;

    private static TripChatPost New(Conversation c, Guid senderId, string type)
        => new(c, new Message
        {
            Id = Guid.NewGuid(), ConversationId = c.Id, SenderId = senderId, Type = type,
            PayloadJson = "{}", CreatedAt = DateTime.UtcNow,
        }, 0, true);
}
