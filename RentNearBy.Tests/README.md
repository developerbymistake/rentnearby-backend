# RentNearBy.Tests

xUnit v3 tests for the backend. Unit tests cover pure logic only (no PostgreSQL, Redis, RabbitMQ, Google or
Cloudinary). DB-backed tests have a ready seam (`Data/`, `Integration/`) and are skipped unless a test database
is configured. Adding tests never needs a change to the csproj, the global usings or the fixtures.

## Folder map

The test project mirrors the production layering. One area folder per domain, one test class per production
class, named `<ProductionClass>Tests`.

| Folder | Holds | Production classes covered today |
|---|---|---|
| `Support/` | Shared builders, fake clock, ids, helpers (no tests) | |
| `Data/` | `DatabaseFixture` (abstract lifecycle), `TestDatabase` (the one fixture tests use), `IntegrationCollection` | |
| `Integration/` | `IntegrationTestBase`, `[IntegrationFact]`/`[IntegrationTheory]` and DB-backed tests (`Category=Integration`) | `DatabaseSmokeTests` |
| `Plans/` | Plan catalog, entitlements, purchases | `PlanOptions` (`PlanCycles`, `PlanFeatures`), `PlanFeatureValues`, `PlanCatalogSnapshot`, `PlanEntitlementService`, `PlanNotifications`, `PlanPurchaseService`, `PlanPurchaseAdminPublisher` |
| `PlayBilling/` | Google Play billing | `PlayStates`, `PlaySubscriptionSnapshot`, `PlayBillingOptions`, `PlayBillingClient`, `PlayEntitlementApplier`, `PlaySyncLogEvents`, `PlayAccountKeys` |
| `Listings/` | Browse, cache keys, slugs | `BrowseCursor`, `ListingCacheKeys`, `SlugGenerator` |
| `Roles/` | Roles and tab gating | `UserRoles`, `RoleChangeRules`, `AppTabKeys`, `TabState` |
| `Notifications/` | Notification constants and routes | `NotificationTypes`, `NotificationDestination`, `UserNotificationRoutes` |
| `Validation/` | FluentValidation validators | `PlanRequestValidators`, `PlayBillingRequestValidators`, coupon validators, `UpsertPlanRequestValidator`, `UpdatePaymentFeatureRequestValidator` |
| `Api/` | Api layer helpers | `ErrorHandlingMiddleware`, `ServiceCollectionExtensions` |
| `Infrastructure/` | Infrastructure helpers | `RabbitMqUrl` |

Reserved for later (create the folder with its first test, no empty folders): `Pricing/`, `Admin/`, `Images/`
(e.g. `Images/PhotoServiceTests`), `Stays/`, `Taxis/`, `Jobs/`, `Chat/`.

## Support/

| File | Gives you |
|---|---|
| `TestIds` | Fixed Guids (`RoomFree`, `RoomPlus`, `PlotFree`, `PlotPlus`, `Price1m/3m/12m`, `User1/2`, `Sub1`, `UserPlan1`, `Purchase1`) |
| `FakeClock`, `Dates` | `FakeClock` for code that takes "now" as a parameter; `Dates.Far` (2099-01-01Z), `Dates.Past` (2020-01-01Z), `Dates.Utc(...)` |
| `CatalogBuilders` | `CatalogPlanBuilder` (default Room Plus: limit 3, priority 2, featured, 1m 299 / 3m 799 / 12m 2999, trial `trial-7d`, product `room_plus`; `Free()`/`AsDefault()`), `CatalogPriceBuilder`, `PlanCatalogSnapshotBuilder` (default Room Free + Room Plus + Plot Free + Plot Plus), `PaymentFeatureStates.On()/Off(freeLimit)` |
| `ActiveRowBuilder` | `PlanEntitlementService.ActiveRow` (internal) |
| `EntityBuilders` | `UserPlanBuilder`, `PlanPurchaseBuilder`, `PlaySubscriptionBuilder`, `CouponBuilder` (in-memory entities, also ready for DB tests) |
| `PlayBuilders` | `PlayLineItemBuilder`, `PlaySnapshotBuilder`, `PlayResolvedTargetBuilder` |
| `Requests` | Fully valid request samples; change one field with `with { ... }` |
| `ValidationAssert` | `HasMessage`, `HasErrorOn(prefix)`, `Valid` for FluentValidation results |
| `RecordingPublisher` | Hand-written `IRabbitMqPublisher` that records publishes or fails like a broker outage |
| `Json` | Google-style JSON from anonymous objects (`Json.Of(new { ... })`, `Json.Empty`) |
| `Culture` | `using var _ = Culture.Invariant();` for culture-dependent formatting |

Every builder has sensible defaults and fluent `With*` overrides; `Build()` returns the production type.

## Naming rules

- File: `<Area>/<ProductionClass>Tests.cs`, namespace `RentNearBy.Tests.<Area>`.
- Method: `Method_State_Expected` (e.g. `Compute_PaymentOffPlusRow_MaxOfDefaultPlanAndFreeLimitButNotPaid`).
- Table-driven cases use `[Theory]` with `[InlineData]`, or `[MemberData]` over a `TheoryData<...>` property for typed rows.
- Expected values come from reading the production code, never from the value a builder was just given (no tautologies).
- No test mutates static state; xUnit runs test classes in parallel.
- Production code reads `DateTime.UtcNow` directly: use far-future/past instants or a +-5 s window, never sleep.

## How to add a unit test

1. New case for an existing rule: add one `[InlineData(...)]` line or one row to the `TheoryData`.
2. New behaviour of a covered class: add a `[Fact]` or `[Theory]` to its `<Class>Tests`.
3. New production class: add `<Area>/<Class>Tests.cs` (new area folder if needed). Nothing else changes.
4. New shared test data: add a builder (defaults + `With*`) under `Support/`.
5. An `internal` production member: Infrastructure already has `InternalsVisibleTo("RentNearBy.Tests")`. Core and Api do not;
   add `<InternalsVisibleTo Include="RentNearBy.Tests" />` to that csproj only when a test truly needs it.

## How to add an integration test

```csharp
public class PlanPurchaseFlowTests(TestDatabase db) : IntegrationTestBase(db)
{
    [IntegrationFact]
    public async Task Grant_NewPurchase_CreatesActiveRow()
    {
        await using var ctx = CreateContext();
        // arrange with Support/ builders, act through the real service, assert on the database
    }
}
```

- Put it under `Integration/`, derive from `IntegrationTestBase` and take `TestDatabase` in the constructor: the
  `Integration` collection (one shared `TestDatabase`, classes run one after another) and
  `[Trait("Category","Integration")]` come from the base.
- Mark every test `[IntegrationFact]` or `[IntegrationTheory]` (never plain `[Fact]`/`[Theory]`): they skip the test
  unless `TestDatabase.IsAvailable`. The skip rule lives only there, so changing it never touches a test.
- Point `RENTNEARBY_TEST_DB` at a disposable PostgreSQL with PostGIS, for example
  `Host=localhost;Port=5432;Database=rentnearby_test;Username=postgres;Password=...`. Never the dev or prod database.
- Schema setup (migrate/reset) goes in `TestDatabase` by overriding `PrepareAsync()`
  (e.g. `await using var ctx = CreateContext(); await ctx.Database.MigrateAsync();`). Tests do not change.
- SQLite and EF InMemory are not used: the model has PostGIS `geography` computed columns, raw SQL, row locks
  and partial unique indexes that only PostgreSQL honours.
- Testcontainers later: change only `TestDatabase`. Add `Testcontainers.PostgreSql` (4.16.0 verified on NuGet);
  `StartAsync()` returns the env var when set, otherwise starts
  `new PostgreSqlBuilder().WithImage("postgis/postgis:<tag>").Build()` and returns its connection string (image tag
  to be chosen and verified then); `DisposeAsync()` stops the container; `IsAvailable` becomes "env var set or
  container mode on". `IntegrationCollection`, `IntegrationTestBase`, the attributes and every test stay as they are.
- If the base-class `[Collection]`/`[Trait]` ever does not reach a derived class on a runner, add the same two
  attributes to that class.

## How to run

From `F:/Dhasu/Backend`:

```bash
# unit tests only
dotnet test RentNearBy.Tests --filter "Category!=Integration"

# everything; [IntegrationFact]/[IntegrationTheory] tests self-skip without RENTNEARBY_TEST_DB
dotnet test RentNearBy.Tests

# integration tests against a real test database (bash)
RENTNEARBY_TEST_DB="Host=localhost;Database=rentnearby_test;Username=postgres;Password=..." dotnet test RentNearBy.Tests --filter "Category=Integration"
```

PowerShell: `$env:RENTNEARBY_TEST_DB = "Host=..."; dotnet test RentNearBy.Tests --filter "Category=Integration"`.

The project is added to the solution with `dotnet sln RentNearBy.sln add RentNearBy.Tests/RentNearBy.Tests.csproj`.
The Docker image builds `RentNearBy.Api` only, so tests never affect the deploy.

## Packages

| Package | Version | Note |
|---|---|---|
| `xunit.v3` | 4.0.2 | v3 test projects are executables (`OutputType Exe`) |
| `xunit.runner.visualstudio` | 4.0.1 | VSTest adapter, keeps `dotnet test --filter` working |
| `Microsoft.NET.Test.Sdk` | 18.11.0 | VSTest host |
| `FluentValidation` | transitive via `RentNearBy.Api` | `FluentValidation.TestHelper` is a namespace inside it, no extra package |

`TestingPlatformDotnetTestSupport` is deliberately not set, so `dotnet test` runs through VSTest.

Contingencies:
- VSTest adapter error with `Microsoft.NET.Test.Sdk` 18.11.0: fall back to the latest 17.x on NuGet.
- .NET 10 SDK or newer: replace `xunit.v3` with `xunit.v3.mtp-off` (same version 4.0.2) to stay on VSTest.

## Production prerequisites

These production members are `internal` (or were made `internal` for tests, no behaviour change) and are reached
through `InternalsVisibleTo("RentNearBy.Tests")` in `RentNearBy.Infrastructure.csproj`:

- `PlanEntitlementService.ActiveRow`, `PlanEntitlementService.Compute`
- `PlanPurchaseService.ReplacementModeOf`, `DiscountOf`, `NormalizeCode`
- `PlayBillingClient.Parse`
- `PlayEntitlementApplier.PayableOf`, `DaysOf`
- `BrowseCursor`, `BrowsePhase`, `RabbitMqUrl` (internal already)

If one of them is `private` again, the build fails in the matching test class.

## Not covered (and why)

- `PlanPurchaseService` quote/order/verify/grant flows (`BuildQuoteAsync`, `BuildPlayQuoteAsync`, `IsCouponUsableAsync`,
  `GrantInTxAsync`, `GrantPlayRowAsync`, `ApplyCapturedAsync`, `AdminGrantAsync`) need `ApplicationDbContext`, row locks
  (`FOR UPDATE`), partial unique indexes and transactions; only the pure helpers are tested. First integration candidates.
- `PlayBillingSyncService` state table (`DispatchAsync`, hold/pause/ended/carry-over, `ProcessRtdnAsync`, voided) and
  `PlayEntitlementApplier.ApplyEntitledInTxAsync`: same reason (transaction + owner/token locks + `PaymentEvents` inbox).
  `CanReattach` is private and takes a private `Mapping` type; not exposed.
- `PlanSlotService`, `PlanLifecycleService`, `PlanPurchaseReconcilerService`, `ListingExpirySweepService`: raw SQL,
  `ExecuteUpdate`, `PeriodicTimer`.
- `PlayBillingClient` HTTP calls and `PlayRtdnAuthenticator` (Google OIDC): external.
- `RabbitMqPublisher` and every `*WorkerService`, including `PlanPurchaseAdminWorkerService`: need a broker.
- `PhotoService`: Cloudinary plus real image bytes (future `Images/PhotoServiceTests` once `ProcessImageAsync` is reachable).
- Handlers/endpoints (`GetStats` query, auth, tab gate filter): need the host and PostgreSQL; `WebApplicationFactory` +
  PostgreSQL is the planned integration route.
- Anything in `Migrations/`.
