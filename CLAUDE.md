# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

ASP.NET Core 9 minimal-API backend for RentNearBy (product/app name in user-facing text: "Bakhli") — a
room/plot rental listings platform with geo-search, chat, payments, and push notifications. PostgreSQL +
PostGIS via EF Core/Npgsql, Redis for OTP/rate-limiting/caching, RabbitMQ for background job queues,
SignalR for realtime chat/banners, Razorpay for payments, Cloudinary for photo storage, Firebase for push.

No test project currently exists in the solution.

## Commands

```bash
# Restore / build (from repo root)
dotnet restore
dotnet build RentNearBy.sln

# Run the API (from repo root or RentNearBy.Api/) — picks up launchSettings.json profiles
dotnet run --project RentNearBy.Api            # http profile -> http://localhost:5000
dotnet watch --project RentNearBy.Api           # hot reload

# EF Core migrations (Infrastructure holds DbContext + migrations, Api is the startup project)
dotnet ef migrations add <Name> --project RentNearBy.Infrastructure --startup-project RentNearBy.Api
dotnet ef database update --project RentNearBy.Infrastructure --startup-project RentNearBy.Api

# Docker (build + run the API image; expects env vars, see below)
docker compose up --build
```

There is no lint/format script configured (no `.editorconfig`, no test project, no `dotnet format` wired
into CI in this repo) — `dotnet build` is the primary correctness check. Because there are no tests, verify
behavior changes by running the API and hitting the affected endpoint (`RentNearBy.Api/RentNearBy.Api.http`
has example requests; Swagger UI is available at `/swagger` in Development).

### Local environment setup

- Copy `RentNearBy.Api/appsettings.example.json` → `RentNearBy.Api/appsettings.json` (and/or
  `appsettings.Development.json`) and fill in `ConnectionStrings:DefaultConnection` and `Jwt:Key`.
- Copy `.env.example` → `.env` for the values `docker-compose.yml` / deployment expect: `DATABASE_URL`,
  `JWT_KEY`, `API_BASE_URL`, `WhatsApp__PhoneNumberId`, `WhatsApp__AccessToken`, `RABBITMQ_URL`,
  `FCM_SERVICE_ACCOUNT_JSON`, plus (used by `docker-compose.yml` directly) `REDIS_URL`,
  `CLOUDINARY_CLOUD_NAME`, `CLOUDINARY_API_KEY`, `CLOUDINARY_API_SECRET`.
- `ServiceCollectionExtensions.AddApplicationServices` resolves the DB connection string as
  `configuration["DATABASE_URL"] ?? configuration.GetConnectionString("DefaultConnection")` — env var wins
  over appsettings. Redis is optional: if `REDIS_URL` is unset, OTP storage and rate limiting silently fall
  back to in-memory implementations (`MemoryOtpStore`, `InMemoryRateLimitService`) instead of the Redis ones.
- `ApplicationDbContextFactory` (used by `dotnet ef` design-time tooling) reads
  `ConnectionStrings__DefaultConnection` from the environment, separately from the app's own resolution
  logic above — keep both in sync when changing the local Postgres connection.

## Architecture

### Project layout (3-project solution, dependency direction: Api → Infrastructure → Core)

- **RentNearBy.Core** — dependency-free domain layer: `Entities/` (EF entities), `DTOs/Requests` and
  `DTOs/Responses`, `Interfaces/` (every repository and service is defined here as an interface —
  `IUnitOfWork`, `I*Repository`, `IJwtService`, `IPaymentService`, etc.). Only external package: NetTopologySuite.
- **RentNearBy.Infrastructure** — EF Core `Data/ApplicationDbContext` (all entity configuration lives in one
  large `OnModelCreating`, not per-entity `IEntityTypeConfiguration` classes), `Repositories/` (generic
  `Repository<T>` + per-entity repositories + `UnitOfWork` facade), `Services/` (everything that talks to an
  external system: Cloudinary, Razorpay, Redis, RabbitMQ, Firebase, WhatsApp OTP, Nominatim/Overpass
  geocoding), `Migrations/`, and `Data/*.json` embedded resources (India district/city boundary seed data
  consumed by `DataSeeder`/`BoundaryImporter`).
- **RentNearBy.Api** — presentation layer, no business logic beyond request shaping: `Endpoints/` (route
  registration only), `Handlers/` (the actual request logic, as static methods — minimal-API style, not
  controllers), `Hubs/` (SignalR), `Validators/` (FluentValidation, one class per request DTO),
  `Middleware/`, `Extensions/`, `Mappings/` (Mapster config).

### Request flow / minimal-API conventions

- **No controllers.** Each domain has `Endpoints/XEndpoints.cs` (a `static` class with a
  `MapXEndpoints(this RouteGroupBuilder group)` extension mapping routes to handler methods) and
  `Handlers/XHandlers.cs` (`static` classes with `static Task<IResult>` methods). `Program.cs` wires each
  group under a versioned prefix, e.g. `app.MapGroup("/api/v1/listings").MapListingsEndpoints()`.
- **DI via minimal-API parameter binding**, not constructor injection: handler methods take the request DTO,
  `IValidator<TRequest>`, `ClaimsPrincipal`, `IUnitOfWork`, and whatever services they need directly as
  method parameters — the framework resolves them per-request.
- **Validation is manual, not an automatic pipeline filter.** Every handler that takes a request body starts
  with `var validation = await validator.ValidateAsync(request); if (!validation.IsValid) return
  BadRequestResponse(...)`. Validators are auto-registered via
  `AddValidatorsFromAssemblyContaining<SendOtpRequestValidator>()` in `Program.cs`, but invoking them is the
  handler's job.
- **Every response goes through `ApiResults` (`RentNearBy.Api/Extensions/ResponseExtensions.cs`)** —
  `OkResponse`, `BadRequestResponse`, `UnauthorizedResponse`, `ForbiddenResponse`, `NotFoundResponse`,
  `ConflictResponse`, `TooManyRequestsResponse`, `ServerErrorResponse` — all wrapping payloads in the same
  `ApiResponse<T>{ Status, Code, Timestamp, Data, Error }` envelope. `ErrorHandlingMiddleware` catches any
  unhandled exception and produces the same envelope shape (mapping exception type → status code), so
  handlers only need to return `ApiResults.*` explicitly for expected error paths.
- **Entity → DTO mapping** uses Mapster (`.Adapt<T>()`), configured once in
  `DtoMappings.ConfigureMappings()` (called before `builder.Build()` in `Program.cs`), not per-call
  configuration.

### Auth

- JWT bearer tokens (`Microsoft.AspNetCore.Authentication.JwtBearer`), issued by `JwtService`
  (`RentNearBy.Infrastructure/Services/JwtService.cs`) with 365-day expiry and custom claims: `session_id`
  and `actor_type` (`"user"` or `"admin"`). There are two parallel actor types with separate session tables
  (`Sessions`/`IUserRepository` side vs `AdminSessions`/`IAdminRepository` side) sharing the same JWT scheme.
- **Sessions are revocable despite being stateless JWTs**: `AuthenticationExtensions.AddJwtAuthentication`'s
  `OnTokenValidated` event looks up the session (by `session_id` claim, routed to `Sessions` or
  `AdminSessions` based on `actor_type`) and calls `context.Fail(...)` if it's missing/expired/revoked — so a
  logout or forced session invalidation takes effect immediately, not just when the JWT itself expires.
- `"AdminOnly"` authorization policy = `RequireClaim("actor_type", "admin")`, applied to admin-only endpoint
  groups (`AdminEndpoints`, `AdminAuthEndpoints`, `BannerEndpoints`'s admin group, `PlotEndpoints`'s admin
  group).
- SignalR can't send the `Authorization` header over its WebSocket handshake, so `OnMessageReceived` also
  accepts the token via `?access_token=` query string — this is how `ChatHub`/`BannerHub` auth works.

### Data / PostGIS

- `ApplicationDbContext` is registered with `AddDbContextPool` (not `AddDbContext`) — pooled/reused across
  requests. This is safe only because the context has no per-request custom state and every consumer
  resolves it as Scoped (see the comment in `ServiceCollectionExtensions`); keep that invariant if you add
  new consumers.
- Spatial search relies on Postgres/PostGIS: `RoomListing`/`PlotListing` have a database-computed `geography
  (Point, 4326)` `Location` column derived from `Latitude`/`Longitude` (`HasComputedColumnSql(...stored:
  true)`), each with a GiST index, used for "nearby" queries. `District.Boundary` is a `geometry` polygon
  column with a **partial** GiST index (`WHERE "IsActive" = true`) used to resolve a lat/lng into a district
  via `ST_Contains` in the `/listings/context` endpoint.
- Several unique indexes exist specifically to close TOCTOU races that the application-level code can't
  prevent on its own (documented inline in `ApplicationDbContext.OnModelCreating`): one Pending report per
  user per listing, one Pending payment transaction per listing/upgrade, one conversation per
  renter/owner/listing tuple. When touching payment, report, or conversation creation logic, preserve these
  DB-level constraints rather than relying on a check-then-insert in the handler.
- **Startup migration behavior differs by environment** (`Program.cs`): in `Development`, every app startup
  **drops all tables** (raw SQL, excluding PostGIS system tables) and rebuilds via `MigrateAsync()`, then
  reseeds — fast local iteration, but destructive. In non-Development, only pending migrations are applied
  (additive, never `EnsureCreatedAsync`, so migration history stays consistent between environments). Do not
  add `EnsureCreatedAsync()` anywhere — it was deliberately avoided because it desyncs
  `__EFMigrationsHistory`.
- `DataSeeder.SeedAsync` runs after migrations on every startup and is idempotent (`if (await
  db.X.AnyAsync()) return;` guards per seed method) — seeds room/plot types, report reasons, question
  templates, plans, India districts/cities (from embedded `Data/gadm41_IND_2.json`, `districts.json`,
  `cities.json`), listing-limit settings, the welcome-bonus coupon, and admin accounts.

### Background processing (RabbitMQ + hosted services)

Two kinds of `IHostedService` are registered in `ServiceCollectionExtensions`:
1. **Timed jobs** (self-scheduling `BackgroundService`s, not queue consumers): `ListingExpirySweepService`
   (replaces the old room/plot membership-expiry jobs — sweeps both `RoomListing`/`PlotListing` by their own
   `ValidUntil`, no membership record involved, runs 12:00 AM IST), `DistrictDigestJobService`
   (aggregates daily digest, 4:00 AM IST), `PendingCoinPurchaseCleanupService` (sweeps abandoned/crashed
   coin-pack purchases every 30 min — two-pass: self-heal a purchase whose wallet credit succeeded but whose
   status flip didn't, then mark genuinely abandoned PENDING rows).
2. **RabbitMQ consumers**, each owning its own connection built from `RabbitMqUrl.Build(configuration)`
   (`RABBITMQ_URL`): `NotificationWorkerService` (`listing.expired`, dead-letters to
   `dlq.listing.expired`), `DlqNotificationWorkerService` (consumes the DLQ), `BroadcastWorkerService`
   (`broadcast.notification`), `DistrictDigestWorkerService` (`district.digest.ready`),
   `ReportFiledWorkerService` (`report.filed`), `ChatMessageNotificationWorkerService`
   (`chat.message.push`). All use manual ack/nack with a reconnect-with-backoff loop.

Publishing side is `IRabbitMqPublisher`/`RabbitMqPublisher` (singleton — the RabbitMQ `IConnection` is
long-lived/expensive), which wraps `PublishAsync` in a Polly retry (3 attempts, exponential backoff).

When adding a new async side-effect (e.g. "notify X when Y happens"), the established pattern is: publish a
small message record (see `ListingExpiredMessage`, `ReportFiledMessage`, etc.) to a new/existing queue via
`IRabbitMqPublisher`, then add a dedicated `BackgroundService` consumer in `Infrastructure/Services` and
register it as a hosted service — rather than doing the side effect inline in the handler.

### Realtime (SignalR)

Two hubs, both requiring the JWT-in-query-string auth described above: `ChatHub` (`/hubs/chat`) and
`BannerHub` (`/hubs/banner`). `ChatHub` uses group-naming conventions `user_{userId}` (always joined, drives
unread-badge/new-message pushes even when no chat screen is open) and `conversation_{conversationId}`
(joined only while that specific thread is open, via a `conversationId` query param).

### Coin economy (replaces the old per-listing Razorpay membership model)

There is no more `RoomMembership`/`PlotMembership`/`PaymentTransaction`/`AppFeature` — these were removed
entirely (final migration `RemoveOldMembershipSystem`). Users spend coins from a `Wallet` to "Go Live" on a
Room/Plot listing; a listing's own `IsActive`/`ValidUntil` fields are the only per-listing state.

- **`ICoinWalletService`/`CoinWalletService`** (`Infrastructure/Services/CoinWalletService.cs`) is the one
  shared spend/credit engine — every feature that moves coins (Go Live, coin-pack purchase, coupon
  redemption, admin manual credit/debit) calls `SpendCoinsAsync`/`CreditCoinsAsync` on it, never its own
  copy of the balance-mutation logic. Mutations are atomic `ExecuteUpdateAsync` (never read-then-write);
  one-shot reasons (`CoinTransactionReasons.OneShotCreditReasons`/`OneShotDebitReasons`) get idempotency via
  a `SAVEPOINT` + a partial unique index on `(UserId, Reason, ReferenceId)`, generated from that same C#
  list so the index filter can't drift from the reason set. `GoLiveHandlers` (Room and Plot) both call this
  identical service — the spend mechanism itself is never duplicated per listing kind.
- **Buying coins**: `CoinPack` (admin-managed catalog) → `CoinPackPurchaseService.CreateOrderAsync`/
  `VerifyAndCreditAsync`, reusing the same `IRazorpayService` order/verify/webhook flow the old membership
  payments used — only what a successful payment grants changed (coins credited to the wallet, not a
  listing-specific membership). `PendingCoinPurchaseCleanupService` self-heals a purchase that crashed
  between the wallet credit and its own status flip.
- **Redeem codes**: `Coupon`/`CouponRedemption` via `ICouponService`, including a fixed seeded
  `WELCOME_SIGNUP` coupon redeemed best-effort on signup completion (see `AuthHandlers.PhoneCompleteOnboarding`).
- **Going live**: `GoLiveHandlers.GoLiveRoom`/`GoLivePlot` (`POST /listings/{id}/go-live`,
  `POST /plots/{id}/go-live`) — deactivating and reactivating a listing within its already-paid
  `ValidUntil` window is free (no spend); going live after expiry (or for the first time) requires a
  `PlanType` and spends `CoinPlan.OriginalPrice` coins. `RoomListing`/`PlotListing` use Postgres `xmin`
  optimistic concurrency (shadow `IsRowVersion()` property, no real column) so two concurrent Go-Live
  attempts on the same listing can't both silently win — surfaced to the caller as a distinct
  `CONCURRENT_UPDATE` error, separate from `INSUFFICIENT_BALANCE`.
- **`CoinFeature` + `CoinPlan`** (replaces the short-lived `GoLivePlan`, which itself replaced the old
  membership-era `RoomPlan`/`PlotPlan` tables): `CoinFeature` is a small seed-only catalog of "what coins
  can be spent on" (`RentNearBy.Core/Models/CoinFeatureKeys.cs` — currently `ROOM_GOLIVE`/`PLOT_GOLIVE`
  only); `CoinPlan` holds the Basic/Standard/Premium tiers, keyed by `FeatureKey` (matches a `CoinFeature.Key`,
  string discriminator — no FK, same convention as `ListingLimitSetting`/`ListingKinds`) with a unique index
  on `(FeatureKey, PlanType)`. `Days` (validity window) and `Quota` (resource quantity — `RoomLimit`/
  `PlotLimit` on the wire) are both always genuinely populated for every plan; the design is deliberately
  feature-agnostic so a future coin-gated feature (e.g. contact reveal, chat) can slot in as a new
  `CoinFeature` row with zero schema change. `CoinPlanTypes` (`RentNearBy.Core/Models/CoinPlanTypes.cs`)
  holds the `Basic`/`Standard`/`Premium` tier-name constants — used by `DataSeeder`, never hardcoded string
  literals. `ICoinPlanRepository`/`CoinPlanRepository` expose `GetByFeatureKeyAndPlanTypeAsync`/
  `GetByFeatureKeyAsync`, wired as `IUnitOfWork.CoinPlans`. `AdminHandlers`' shared
  `GetCoinPlansCoreAsync`/`CreateCoinPlanCoreAsync`/`UpdateCoinPlanCoreAsync` trio backs both the Room and
  Plot admin plan CRUD endpoints from one implementation (discriminated by `FeatureKey`) — the update path
  guards `plan.FeatureKey != featureKey` so a Room-scoped admin request can't mutate a Plot plan by ID
  (security-relevant now that both share one physical table). `CoinPack` (the Starter/Popular/Mega
  real-money recharge tiers) is a separate, already-correct, feature-agnostic system and is untouched by
  any of this.
- **Listing-creation cap** (separate concept from being live): `ListingLimitSetting` (2 seeded rows,
  Room/Plot) is a dedicated admin-configurable entity — never repurpose it as a concurrent-active-listing
  cap; it only gates `POST /listings`/`POST /plots` creation, exposed publicly via the cached
  `GET /config/listing-limits`.
- **Admin's lever**: no free-activation bypass around the spend engine — `ToggleAdminListingStatus`/the plot
  equivalent only checks the listing's own `ValidUntil` (deactivate is always allowed, pure moderation).
  Admin's real tool is `POST /admin/users/{id}/wallet/credit`/`.../debit` (idempotent via a client-supplied
  `IdempotencyKey`), after which the owner does their own Go-Live.

### Services marketplace, Agents & Leads (separate vertical from Room/Plot)

A second product surface, unrelated to Room/Plot listing CRUD, sharing only platform infra (JWT auth,
SignalR, RabbitMQ/FCM push): `ServiceCategory` → `Service` → `ServicePackage` — **Categories are the
catalog's top level** (the old `ServiceSection` layer was removed end-to-end by the
`RemoveServiceSectionsAndResetCatalog` migration, which also reset all catalog + inquiry data so the
seeder rebuilt the 3-category structure: Char Dham Yatra, Tour Travel & Camping, Yoga & Diet). Each
active category renders as one color-zoned rail in the consumer app. Offerings are Travel/Event/
Consultation per `ServiceCategory.FormType` (`RentNearBy.Core/Models/ServiceCategoryFormTypes.cs`),
which decides which optional fields the consumer inquiry form shows
(`PreferredDateOrTripStart`/`NumberOfPeople`); every Consultation (Yoga & Diet) package is Price=null →
"Get Custom Quote" (middleman model — the agent quotes offline). The consumer rail preview is
`GET /services/preview?serviceCategoryId=`; inquiry DTOs carry `ServiceCategoryId`/`ServiceCategoryName`
(the admin list filter is `serviceCategoryId`). A TEMPORARY `GET /services/sections` stub returns `[]`
for pre-redesign consumer builds — remove it once those builds age out.

- **`Agent`** (`RentNearBy.Core/Entities/Agent.cs`) is a staff/rep role layered on an existing consumer
  `User` account (`Agent.UserId`, required and immutable after creation) — **not a separate login/identity**.
  Admin CRUDs agents and bulk-assigns which `ServiceCategory`s each handles
  (`PUT /agents/{id}/categories`). Agent-facing routes (`GET /agents/me`, `GET /agents/me/leads`,
  `GET /agents/me/leads/{id}`, `PUT /agents/me/leads/{id}/status`) always resolve identity from the JWT
  `UserId`, never a client-supplied agent id.
- **`Inquiry`** rows are the "leads" — created via `POST /inquiries` against a `Service`/`ServicePackage`.
  `CreateInquiry` auto-assigns every active agent mapped to that service's category and auto-transitions
  `Submitted → Contacted` in one write (`InquiryHandlers.cs`). Assignment is many-to-many via
  `InquiryAgent` (a join entity that replaced an older single-FK "AssignedAgent" concept — migration
  `ReplaceAssignedAgentWithInquiryAgentJoin`), so multiple agents can each see the same lead in their own
  "My Leads." Admin can re-set the full agent list on an inquiry (`PUT /admin/inquiries/{id}/agents`), and
  the handler enforces server-side that any agent assigned is actually mapped to the inquiry's own service
  category (defense against a direct-API bypass of the admin UI's own filtering).
- **`InquiryEscalation`** — a consumer's self-service "report an issue with my agent"
  (`POST /inquiries/{id}/escalate`, fixed reason enum in `EscalationReasons.cs`:
  `NotResponding`/`Unhelpful`/`WrongInformation`/`Other`), resolved by Admin only
  (`PUT /admin/inquiries/{id}/escalation/resolve`) and never surfaced back to the assigned agent. Blocked if
  no agent is assigned yet; a DB partial-unique-index (not just app logic) prevents duplicate open
  escalations, caught as `DbUpdateException` → `409`.
- This vertical has no coin/payment gating — submitting an inquiry is free lead generation; `ServicePackage`
  pricing is informational display only, not charged through `CoinWalletService`.

### Notification inbox/push

Generic, reusable in-app notification system (`NotificationEvent`/`NotificationRead`,
`RentNearBy.Core/Models/NotificationTypes.cs`), currently emitting only `LeadAssigned` but designed for
zero-schema-change extension to other types. One row per send, not per recipient — `NotificationRead` rows
are created lazily only when actually read, so it stays cheap even for a future broadcast-to-many.
Consumer-facing (`/api/v1/notifications`): `POST/DELETE register-token`, `GET /` (paginated inbox),
`GET /unread-count`, `PUT /{id}/read`, `PUT /read-all` — `MarkNotificationRead` always returns `OkResponse`
regardless of whether the id exists/belongs to the caller, deliberately avoiding an existence-leak oracle.
Admin-facing (`GET /admin/notifications`) is a system-wide read-only feed, unscoped per-admin. Delivery
follows the same dual SignalR + RabbitMQ/FCM push shape as `ChatMessageNotificationWorkerService`
(`NotificationPushWorkerService`/`NotificationPushPayload`).

### Known gap: district validation is not enforced server-side at listing creation

`CreateListing` (`ListingsHandlers.cs`) and `CreatePlotListing` (`PlotHandlers.cs`) only validate
`DistrictId` via `NotEmpty()` — never `District.IsActive`, never `ST_Contains(District.Boundary, point)`
against the submitted `Latitude`/`Longitude`, despite that boundary check existing and being used elsewhere
(`GET /listings/context`). The only district-adjacent check is that a client-supplied `CityId` (optional)
must belong to the client-supplied `DistrictId` — if `CityId` is omitted, `DistrictId` isn't validated
against anything. A client can currently submit a real pin in one place while claiming any `DistrictId`
(active or not), and the listing saves and surfaces in that district's `/nearby` results at the wrong
location. Relevant when touching listing creation or district-gating logic.

### External services (Infrastructure/Services)

Photo storage: Cloudinary (`IPhotoService`/`PhotoService`, config via `CLOUDINARY_*` env vars). Geocoding:
Nominatim (`IGeocodingService`) and Overpass (`IOverpassService`) via typed `HttpClient`s pointed at
`nominatim.developerbymistake.tech`. Push: Firebase Admin SDK (`IFcmService` for membership/broadcast/digest
notifications, and a separate `IChatFcmService` for chat pushes — both Singleton because
`FirebaseApp.Create()` must only run once). OTP delivery: WhatsApp Business API (`IOtpService`/
`WhatsAppOtpService`).

### Other conventions

- CORS is locked to a single hardcoded origin (`https://developerbymistake.tech`) in `Program.cs` — update
  there if a new frontend origin needs access.
- `GET /health` reports Redis and Cloudinary connectivity; `GET /delete-account` serves a static HTML page
  (required for Play Store account-deletion compliance) — both are defined inline in `Program.cs` rather
  than as Endpoints/Handlers, unlike everything else.
- All business API routes are versioned under `/api/v1/...` and grouped/tagged by domain in `Program.cs`
  (auth, admin-auth, users, listings, admin, plots, admin/plots, account, notifications, admin/notifications,
  payments, banners, admin banners, chat, home, agents, inquiries, admin/inquiries). Swagger UI is only
  mapped in Development.
