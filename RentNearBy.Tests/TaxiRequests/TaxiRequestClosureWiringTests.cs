using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RentNearBy.Api.Extensions;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Infrastructure.Services.TaxiRequests;

namespace RentNearBy.Tests.TaxiRequests;

// MASTER_PLAN B2-8: every existing closure entry point also runs the taxi request closure, so all current callers
// (taxi delete, admin unpublish and delete, role change, admin role change, account deletion, trip delete, block) are covered.
public class TaxiRequestClosureWiringTests
{
    private static readonly Dictionary<string, string> TaxiCallOf = new()
    {
        [nameof(ITripEnquiryClosure.OnListingsRemovedAsync)] = "t.ListingRemovedAsync(db, id)",
        [nameof(ITripEnquiryClosure.OnTripDeletedAsync)] = "t.TripDeletedAsync(db, tripId)",
        [nameof(ITripEnquiryClosure.OnTravellerDeletedAsync)] = "t.TravellerAccountDeletedAsync(db, userId)",
        [nameof(ITripEnquiryClosure.OnBlockAsync)] = "t.UsersBlockedAsync(db, userA, userB)",
    };

    // A cleared local day rate only touches legacy local sightseeing leads: it is not a taxi request eligibility rule.
    private static readonly string[] NotTaxiRequestEvents = [nameof(ITripEnquiryClosure.OnTaxiRateClearedAsync)];

    [Fact]
    public void Every_closure_entry_point_calls_the_taxi_closure()
    {
        var entryPoints = typeof(ITripEnquiryClosure).GetMethods().Select(m => m.Name)
            .Where(n => n != nameof(ITripEnquiryClosure.AfterCommitAsync)).ToHashSet();
        Assert.Equal(entryPoints.Order(), TaxiCallOf.Keys.Concat(NotTaxiRequestEvents).Order());

        var source = ClosureSource();
        foreach (var (method, call) in TaxiCallOf)
            Assert.True(Body(source, method).Contains($"RunTaxiAsync(t => {call}", StringComparison.Ordinal), $"{method} does not run {call}");

        Assert.DoesNotContain("LocalDayRate", TaxiCandidateQuery.StaticRule);
        Assert.DoesNotContain("LocalDayRate", TaxiCandidateQuery.RequestRule);

        var removed = Body(source, nameof(ITripEnquiryClosure.OnListingsRemovedAsync));
        Assert.Matches(new Regex(@"if \(kind == ListingKinds\.Taxi\)\s*foreach \(var id in listingIds\.Distinct\(\)\.Order\(\)\)\s*await RunTaxiAsync\(t => t\.ListingRemovedAsync\(db, id\), poolChanged: true\);"), removed);
        Assert.True(removed.IndexOf("RunTaxiAsync(", StringComparison.Ordinal) < removed.IndexOf("CloseEnquiriesOfRemovedListingsAsync(", StringComparison.Ordinal),
            "Taxi requests are locked before the enquiry part's save flushes a pending listing write (lock order request -> listing)");
        Assert.Contains("return await CloseEnquiriesOfRemovedListingsAsync(kind, listingIds);", removed);

        // The block route has no transaction of its own; the taxi part opens one when none is current.
        var block = Body(source, nameof(ITripEnquiryClosure.OnBlockAsync));
        Assert.Contains("db.Database.CurrentTransaction is not null", block);
        Assert.Contains("await tx.CommitAsync();", block);
    }

    [Fact]
    public void Taxi_signals_wait_for_after_commit_and_push_rows_are_saved_in_the_transaction()
    {
        var source = ClosureSource();
        var run = Body(source, "RunTaxiAsync");
        var step = run.IndexOf("await step(taxiRequests);", StringComparison.Ordinal);
        var save = run.IndexOf("await db.SaveChangesAsync();", StringComparison.Ordinal);
        Assert.True(step > 0 && save > step, "Push rows written by the taxi closure are saved in the caller's transaction");
        Assert.DoesNotContain("AfterCommitAsync", run);

        var after = Body(source, nameof(ITripEnquiryClosure.AfterCommitAsync));
        Assert.Contains("TaxiRequestClosure.Merge(_taxi)", after);
        Assert.Contains("await taxiRequests.AfterCommitAsync(taxi, poolChanged);", after);
    }

    [Fact]
    public void Enquiry_closure_takes_the_taxi_closure_from_di()
    {
        var ctor = Assert.Single(typeof(TripEnquiryClosure).GetConstructors());
        var p = Assert.Single(ctor.GetParameters(), x => x.ParameterType == typeof(TaxiRequestClosure));
        Assert.True(p.HasDefaultValue);
        Assert.True(typeof(ITaxiRequestClosure).IsAssignableFrom(typeof(TaxiRequestClosure)));
    }

    [Fact]
    public void Availability_change_calls_closure_and_bump()
    {
        var source = HandlerSource();
        var availability = HandlerBody(source, "UpdateAvailability");
        var begin = availability.IndexOf("await unitOfWork.BeginTransactionAsync();", StringComparison.Ordinal);
        var offline = availability.IndexOf("if (paused) taxiSignals = await taxiRequests.DriverOfflineAsync(db, taxi.Id);", StringComparison.Ordinal);
        var save = availability.IndexOf("await unitOfWork.SaveChangesAsync();", StringComparison.Ordinal);
        var commit = availability.IndexOf("await unitOfWork.CommitTransactionAsync();", StringComparison.Ordinal);
        var after = availability.IndexOf("await taxiRequests.AfterCommitAsync(taxiSignals, poolChanged: true);", StringComparison.Ordinal);
        Assert.True(begin > 0 && offline > begin, "Offline closure runs inside the transaction");
        Assert.True(save > offline, "Requests are closed before the listing row is written (lock order request -> listing)");
        Assert.True(commit > save && after > commit, "Signals and the pickup bump run after commit");
        Assert.Contains("var resumed = !wasAvailable && taxi.IsAvailable;", availability);
        Assert.Contains("resumed ? TaxiRequestClosure.DriverOnline(taxi.Id) : TaxiClosureResult.Empty", availability);

        Assert.True(availability.IndexOf("sp.GetRequiredService<TaxiRequestClosure>()", StringComparison.Ordinal) is var resolve && resolve > 0 && resolve < begin,
            "The closure is resolved before the transaction starts");

        var routes = HandlerBody(source, "UpsertRoutes");
        var resolvePlaces = routes.IndexOf("var pickupPlaces = sp.GetRequiredService<TaxiPickupPlaces>();", StringComparison.Ordinal);
        var firstWrite = routes.IndexOf("db.TaxiRoutes.Add(route);", StringComparison.Ordinal);
        var routesSave = routes.IndexOf("await unitOfWork.SaveChangesAsync();", StringComparison.Ordinal);
        var bump = routes.IndexOf("await pickupPlaces.BumpAsync();", StringComparison.Ordinal);
        Assert.True(resolvePlaces > 0 && firstWrite > resolvePlaces, "Pickup places are resolved before any write, never after the commit");
        Assert.True(routesSave > 0 && bump > routesSave, "Route save bumps the pickup lists after its save");

        // O12: the route closure runs inside the save transaction, before the listing row is written; signals after commit.
        var resolveClosure = routes.IndexOf("var taxiRequests = sp.GetRequiredService<TaxiRequestClosure>();", StringComparison.Ordinal);
        var routesBegin = routes.IndexOf("await unitOfWork.BeginTransactionAsync();", StringComparison.Ordinal);
        var routeClosure = routes.IndexOf("taxiSignals = await taxiRequests.RoutesChangedAsync(db, taxi.Id, routeKeys);", StringComparison.Ordinal);
        var routesCommit = routes.IndexOf("await unitOfWork.CommitTransactionAsync();", StringComparison.Ordinal);
        var routesAfter = routes.IndexOf("await taxiRequests.AfterCommitAsync(taxiSignals, poolChanged: false);", StringComparison.Ordinal);
        Assert.True(resolveClosure > 0 && resolveClosure < firstWrite, "The route closure is resolved before any write");
        Assert.True(routesBegin > firstWrite && routeClosure > routesBegin && routesSave > routeClosure, "Offers close before the routes and listing are written");
        Assert.True(routesCommit > routesSave && routesAfter > routesCommit && bump > routesCommit, "Signals and the pickup bump run after commit");
        var lookups = routes.IndexOf("var (_, routeKeys) = await TaxiRouteCityKeys.ResolveForSaveAsync(resolver, saved,", StringComparison.Ordinal);
        Assert.True(lookups > 0 && lookups < routesBegin, "City lookups stay outside the transaction and give the closure its keys");
        Assert.Contains("catch (Exception ex) when (ex is DbUpdateConcurrencyException || PgRetry.IsRetryable(ex))", routes);

        Assert.Single(Regex.Matches(source, Regex.Escape("GetRequiredService<TaxiPickupPlaces>()")));
        Assert.Single(Regex.Matches(source, Regex.Escape("DriverOfflineAsync(")));
    }

    // Confirm locks the request and then the driver listing FOR UPDATE; the driver's delete must take them in the same order.
    [Fact]
    public void Taxi_delete_runs_the_closure_before_writing_the_listing()
    {
        var delete = HandlerBody(HandlerSource(), "DeleteMine");
        var begin = delete.IndexOf("await unitOfWork.BeginTransactionAsync();", StringComparison.Ordinal);
        var closure = delete.IndexOf("tripPushes = await tripClosure.OnListingsRemovedAsync(ListingKinds.Taxi, [taxi.Id]);", StringComparison.Ordinal);
        var save = delete.IndexOf("await unitOfWork.SaveChangesAsync();", StringComparison.Ordinal);
        var commit = delete.IndexOf("await unitOfWork.CommitTransactionAsync();", StringComparison.Ordinal);
        Assert.True(begin > 0 && closure > begin, "The closure runs inside the transaction");
        Assert.True(save > closure && commit > save, "The listing row is written after the closure, before the commit");
        Assert.Single(Regex.Matches(delete, Regex.Escape("SaveChangesAsync()")));
    }

    // Admin unpublish and report takedown (Approved -> Rejected) close the driver's live taxi request offers in the same
    // transaction, before the listing row is written; signals and the pickup bump wait for the commit.
    [Fact]
    public void Admin_takedown_runs_the_taxi_closure_before_writing_the_listing()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "RentNearBy.Api", "Handlers", "AdminTaxiHandlers.cs"));
        var start = source.IndexOf("private static async Task<IResult> TakeDownCoreAsync(", StringComparison.Ordinal);
        Assert.True(start > 0, "TakeDownCoreAsync not found");
        var next = Regex.Match(source[(start + 1)..], @"\n    (public|private|internal) static ");
        var body = source.Substring(start, next.Index + 1);

        var resolve = body.IndexOf("var taxiRequests = sp.GetRequiredService<TaxiRequestClosure>();", StringComparison.Ordinal);
        var begin = body.IndexOf("await unitOfWork.BeginTransactionAsync();", StringComparison.Ordinal);
        var closure = body.IndexOf("var taxiSignals = await taxiRequests.ListingTakenDownAsync(db, taxi.Id);", StringComparison.Ordinal);
        var status = body.IndexOf("taxi.Status = TaxiStatuses.Rejected;", StringComparison.Ordinal);
        var save = body.IndexOf("await unitOfWork.SaveChangesAsync();", StringComparison.Ordinal);
        var commit = body.IndexOf("await unitOfWork.CommitTransactionAsync();", StringComparison.Ordinal);
        var after = body.IndexOf("await taxiRequests.AfterCommitAsync(taxiSignals, poolChanged: true);", StringComparison.Ordinal);

        Assert.True(resolve > 0 && begin > resolve, "The closure is resolved before the transaction starts");
        Assert.True(closure > begin, "The taxi closure runs inside the takedown transaction");
        Assert.True(status > closure && save > closure, "Requests are closed before the listing row is written (lock order request -> listing)");
        Assert.True(commit > save && after > commit, "Signals and the pickup bump run after commit");
        Assert.DoesNotContain("ListingRemovedAsync", body);

        Assert.Contains("=> TakeDownCoreAsync(report.ListingId,", source);
        Assert.Contains("return await TakeDownCoreAsync(id, request.Reason,", source);
    }

    [Fact]
    public void Takedown_closes_offers_like_a_removal_but_never_reads_bookings()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "RentNearBy.Infrastructure", "Services", "TaxiRequests", "TaxiRequestClosure.cs"));
        Assert.Contains("ListingRemovedAsync(ApplicationDbContext db, Guid driverListingId, CancellationToken ct = default)\n        => CloseListingAsync(db, driverListingId, cancelBookings: true, ct);", source.Replace("\r\n", "\n"));
        Assert.Contains("ListingTakenDownAsync(ApplicationDbContext db, Guid driverListingId, CancellationToken ct = default)\n        => CloseListingAsync(db, driverListingId, cancelBookings: false, ct);", source.Replace("\r\n", "\n"));

        var core = Body(source, "CloseListingAsync");
        Assert.Contains("cancelBookings ? await RowsAsync(db, BookingsOfDriverSql, ct, Uuid(\"id\", driverListingId)) : []", core);
        Assert.Contains("CloseOfferAsync(db, request, o.Id, OfferStates.Live, OfferEndReasons.ListingRemoved, tellTraveller: true, ct)", core);
        Assert.Contains("CancelBookingForRemovedListingAsync(db, request, b.Id, acc, ct)", core);

        Assert.NotNull(typeof(ITaxiRequestClosure).GetMethod(nameof(ITaxiRequestClosure.ListingTakenDownAsync)));
    }

    // The app container must hand the enquiry closure a real taxi closure; an optional parameter would otherwise get null
    // silently and every removal, role change, account deletion, trip delete and block would skip the taxi requests.
    [Fact]
    public void App_container_resolves_the_taxi_closure_pickup_places_and_a_wired_enquiry_closure()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DATABASE_URL"] = "Host=localhost;Database=di_only;Username=test;Password=test",
                // TaxiAckTokens (pulled in by the push writer) signs with the JWT key, as in TaxiRequestDiTests.
                ["Jwt:Key"] = "unit-test-signing-key-unit-test-signing-key",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        // Program.cs adds SignalR next to the app services; the enquiry closure's chat service needs its hub contexts.
        services.AddSignalR();
        services.AddApplicationServices(configuration);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.Same(scope.ServiceProvider.GetRequiredService<TaxiRequestClosure>(), scope.ServiceProvider.GetRequiredService<ITaxiRequestClosure>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<TaxiPickupPlaces>());
        var closure = Assert.IsType<TripEnquiryClosure>(scope.ServiceProvider.GetRequiredService<ITripEnquiryClosure>());
        Assert.True(closure.RunsTaxiClosure, "TripEnquiryClosure was built without the taxi request closure");
    }

    [Fact]
    public void Driver_online_frees_the_driver_and_changes_nothing_else()
    {
        var id = Guid.NewGuid();
        var result = TaxiRequestClosure.DriverOnline(id);
        Assert.Equal([id], result.FreedDriverListingIds);
        Assert.Empty(result.CityKeys);
        Assert.Empty(result.Changes);
    }

    [Fact]
    public void Merge_keeps_each_city_driver_and_the_newest_request_version_once()
    {
        var r = Guid.NewGuid();
        var d = Guid.NewGuid();
        var trip = Guid.NewGuid();
        var user = Guid.NewGuid();
        var merged = TaxiRequestClosure.Merge([
            new TaxiClosureResult([7], [d], [new TaxiRequestChange(r, trip, user, 3, "Collecting")]),
            new TaxiClosureResult([7, 8], [d], [new TaxiRequestChange(r, trip, user, 5, "Open")]),
        ]);
        Assert.Equal([7L, 8L], merged.CityKeys.Order());
        Assert.Equal([d], merged.FreedDriverListingIds);
        var change = Assert.Single(merged.Changes);
        Assert.Equal(5, change.Version);
        Assert.Same(TaxiClosureResult.Empty, TaxiRequestClosure.Merge([]));
    }

    [Fact]
    public void Offline_sql_reads_only_ringing_offers_and_removal_reads_live_offers_and_active_bookings()
    {
        Assert.Contains("\"Status\" = 'Ringing'", TaxiRequestClosure.RingingOffersOfDriverSql);
        Assert.DoesNotContain("Accepted", TaxiRequestClosure.RingingOffersOfDriverSql);
        Assert.Contains("'Ringing', 'Accepted', 'Selected'", TaxiRequestClosure.OffersOfDriverSql);
        Assert.Contains("'Booked', 'InProgress'", TaxiRequestClosure.BookingsOfDriverSql);
        Assert.Contains("o.\"DriverUserId\" = @a AND r.\"TravellerUserId\" = @b", TaxiRequestClosure.OffersBetweenUsersSql);
        Assert.Contains("o.\"DriverUserId\" = @b AND r.\"TravellerUserId\" = @a", TaxiRequestClosure.OffersBetweenUsersSql);
        Assert.Contains("ORDER BY \"Id\"", TaxiRequestClosure.ActiveRequestsOfTravellerSql);
    }

    // The pickup and drop lists follow every listing change, a district toggle and a backfilled route key (issue 2).
    [Fact]
    public void Taxi_cache_invalidation_moves_the_pickup_generation()
    {
        var handlers = HandlerSource().Replace("\r\n", "\n");
        var start = handlers.IndexOf("public static async Task InvalidateAsync(IConnectionMultiplexer? redis, Guid? districtId)", StringComparison.Ordinal);
        Assert.True(start >= 0, "TaxiCache.InvalidateAsync not found");
        var invalidate = handlers[start..handlers.IndexOf("\n    }", start, StringComparison.Ordinal)];
        Assert.Contains("await TaxiPickupPlaces.BumpAsync(redis);", invalidate);

        var admin = File.ReadAllText(Path.Combine(RepoRoot(), "RentNearBy.Api", "Handlers", "AdminHandlers.cs"));
        Assert.Contains("TaxiPickupPlaces.BumpAsync(sp.GetService<IConnectionMultiplexer>());", HandlerBody(admin, "ToggleDistrictActive"));

        var backfill = File.ReadAllText(Path.Combine(RepoRoot(), "RentNearBy.Infrastructure", "Services", "TaxiRequests", "TaxiRouteCityKeys.cs"));
        Assert.Contains("await closure.AfterCommitAsync(signals, poolChanged: true);", backfill);
        Assert.DoesNotContain("await closure.AfterCommitAsync(signals, poolChanged: false);", backfill);
    }

    private static string ClosureSource()
        => File.ReadAllText(Path.Combine(RepoRoot(), "RentNearBy.Infrastructure", "Services", "TripEnquiryClosure.cs"));

    private static string HandlerSource()
        => File.ReadAllText(Path.Combine(RepoRoot(), "RentNearBy.Api", "Handlers", "TaxiHandlers.cs"));

    // From the method signature to the next member signature at class level.
    private static string Body(string source, string method)
    {
        var start = Regex.Match(source, @"\n    (public|private) async Task(<[^\n]*?>)? " + method + @"\(");
        Assert.True(start.Success, $"{method} not found");
        var next = Regex.Match(source[(start.Index + 1)..], @"\n    (public|private|internal|//)");
        return next.Success ? source.Substring(start.Index, next.Index + 1) : source[start.Index..];
    }

    private static string HandlerBody(string source, string method)
    {
        var start = Regex.Match(source, @"\n    public static async Task<IResult> " + method + @"\(");
        Assert.True(start.Success, $"{method} not found");
        var next = Regex.Match(source[(start.Index + 1)..], @"\n    (public|private|internal) static ");
        return next.Success ? source.Substring(start.Index, next.Index + 1) : source[start.Index..];
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RentNearBy.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found");
    }
}
