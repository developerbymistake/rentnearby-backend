# Scale runbook (Trip flow: bulk enquiry, reminders, city covers)

Targets: 10,000 concurrent users, bulk enquiry of 3 recipients p95 under 500 ms server time, reminder run over 200,000 pending items with constant memory, no full scans.

## What exists

| Piece | File | Needs PostgreSQL |
|---|---|---|
| City cover thumbnails (rule, bounded parallelism 8, single flight, failure tolerated) | `RentNearBy.Infrastructure/Services/TripCityCatalog.cs`, `RentNearBy.Core/Utils/CloudinaryThumb.cs` | No (unit tests) |
| 10,000 concurrent bulk enquiry of 3 | `RentNearBy.Tests/Integration/ScaleBulkEnquiryLoadTests.cs` | Yes |
| 200,000 row reminder due scan + EXPLAIN ANALYZE | `RentNearBy.Tests/Integration/ScaleReminderRunBenchmark.cs` | Yes |
| Driver, percentiles, RSS sampler, EXPLAIN helper | `RentNearBy.Tests/Integration/ScaleSupport.cs` | No |

## Run

Use a throw-away database with the current migration applied (the fixture migrates it). Never point this at production.

```
set RENTNEARBY_TEST_DB=Host=localhost;Database=rnb_scale;Username=postgres;Password=...
set RNB_SCALE_USERS=10000        (virtual users, all in flight at once)
set RNB_SCALE_HOSTS=300          (stay listings shared by the users)
set RNB_SCALE_REMINDER_ROWS=200000
set RNB_SCALE_P95_MS=500
dotnet run --project RentNearBy.Tests -- -class "RentNearBy.Tests.Integration.ScaleBulkEnquiryLoadTests"
dotnet run --project RentNearBy.Tests -- -class "RentNearBy.Tests.Integration.ScaleReminderRunBenchmark"
```

Without `RENTNEARBY_TEST_DB` both tests are skipped (they never run in the normal suite or CI). Output lines (latency percentiles, rows written, RSS, EXPLAIN plans) go to the xunit test output.

## What to read in the result

- Bulk: `failed=0`, enquiry rows written equals users x 3 (nothing silently dropped), `p95` under the budget. The harness drives the real handler through `JourneyHarness.AskAsync` (real database, fake push and chat), so it measures the DB transaction path; RabbitMQ publishing is the async outbox relay and is not in this number. Setup of 10,000 users and trips runs 16 at a time and takes minutes.
- Reminder: `hosts` equals 500 (every host found once), `pages` about hosts / page size, RSS growth under 200 MB, and the expiry plan must not show `Seq Scan on "TripEnquiries"`. In the host due plan look for an index scan on `ix_tripenquiries_hostreminder` (partial, `Status IN ('Asked','Countered')`); if the planner picks a scan of `ix_tripenquiries_owner_status` instead, compare `Execution Time` and `Buffers` before changing the index.
- The host scan groups by `OwnerUserId` and keysets on it (`OwnerUserId > last`), so memory is one page at any time regardless of the row count.

## Measured (performance pass, PostgreSQL 18.3 on the dev machine, no PostGIS)

PostGIS is not installed, so the full migration and the Scale* tests could not run. The five Trip tables and their real indexes (from `dotnet ef migrations script`, foreign keys stripped) were loaded into a scratch database and the exact SQL of `TripReminderRunner`, `OutboxRelayService` and `TripTimerService` was run with `EXPLAIN (ANALYZE, BUFFERS)`. Data: 400,000 TripEnquiries (200,000 Asked to 5,000 hosts, 200,000 settled or closed), 200,000 TripShortlistItems (100,000 Active), 70,000 Trips. Single connection, warm cache, laptop disk.

| Query | Before | After | Fix |
|---|---|---|---|
| Reminder traveller keyset (one 2,000-user chunk) | 517 to 622 ms, hashed seq scan of all TripEnquiries (x2) | 156 to 165 ms, index only scan | `ix_tripenquiries_settled` (TripId, StepKey) WHERE Status settled |
| Reminder shortlist active chunk | 90 ms | 34 ms | same index |
| Reminder host keyset / host chunk (2,000 hosts, 80,000 rows) | 110 to 170 ms / 200 to 260 ms | unchanged, uses `ix_tripenquiries_hostreminder` | none needed |
| Expiry pass (500 rows) | 0.15 ms | unchanged | none needed |
| Outbox claim, 400 batches of 500 over 200,000 pending plus 1,000,000 published | 40.5 s (PK scan walked the published rows) | 11.3 s, about 17,800 rows/s, index ordered | ORDER BY "NextAttemptAt", "Id" so `ix_outbox_due` serves the order |
| Reminder outbox insert (unnest, ON CONFLICT DO NOTHING, 2,000 per batch) | 200,000 rows in 9.1 s | n/a | none; a replayed batch inserts 0 rows in 18 ms |
| Shortlist close pass (Booked update, every 5 min) | 115 to 123 ms, seq scan of the table | 78 to 82 ms, index scan | `ix_shortlist_closes` now covers Active and Sent |

Estimated reminder run at this size: 35 traveller chunks and 3 host chunks, roughly 35 x (0.16 + 0.03 + 0.03) + 3 x 0.4 s of query time, plus the outbox inserts above. The full run through `TripReminderRunner.RunAsync` was not executed, so this is an estimate from per-chunk numbers, not a measurement. Memory was not measured.

Code hot spot fixed: the stay and taxi host counters (`TripEnquiriesReceived`, `TripLeadsReceived`) were bumped inside the bulk transaction, so travellers sending to one popular host queued on its row lock and two requests with overlapping hosts could deadlock. They are now bumped after commit.

## Not measured

Not run: the 10,000-user bulk enquiry test and the 200,000-row `ScaleReminderRunBenchmark` (both need PostGIS for the full migration), the full `TripReminderRunner.RunAsync` loop and its memory, RabbitMQ publisher confirms and push delivery latency, FCM multicast, the dead-letter flow, and any HTTP-path latency (p95 of the bulk enquiry is unmeasured). Do not quote those targets as achieved until the tests above have run on a PostGIS database with a broker.

## Cover thumbnails (design notes)

- Covers are resolved once per catalog reload inside the existing single-flight lock, from the spot cache, 8 at a time; a failure leaves that city on its own image (`coverUrl` falls back to `imageUrl` in the DTO).
- Only Ready cities without an image and with spots touch the spot cache, so a reload with images set costs zero extra calls.
- Snapshot `Format` is 3: older Redis copies are rejected and rebuilt. `GET /trips/cities` sends `Cache-Control: private, max-age=60` and an ETag that includes the format when unfiltered.
- Thumbnail transformation is `c_fill,w_480,h_360,q_auto,f_auto`; non-Cloudinary URLs pass through unchanged.

## Outbox backlog and the reminder run (alerts)

- Outbox relay. A publish that fails because the RabbitMQ channel or connection is unavailable does NOT count an attempt (the publisher marks it `broker unavailable:`); the row is retried every 15 s. Only a per-message nack or return counts an attempt and backs off 2, 4, ... 300 s. A row is given up by AGE only: `CreatedAt` older than 72 h (the life of the enquiry). There is no attempt limit any more, so a broker outage of any length loses no push that is still useful.
- Backlog metric. `OutboxRelayService` checks once a minute: count and age of rows with `PublishedAt IS NULL`. Log event `outbox.backlog` (EventId 7001): Warning when the oldest row is older than 5 min, Critical when older than 30 min or when any row passed 72 h. Alert on that event name (Critical pages). Same numbers by hand:
  `SELECT count(*), min("CreatedAt"), now() - min("CreatedAt") AS oldest FROM "OutboxMessages" WHERE "PublishedAt" IS NULL;`
  Rows given up (older than 72 h and unpublished): `SELECT count(*) FROM "OutboxMessages" WHERE "PublishedAt" IS NULL AND "CreatedAt" < now() - interval '72 hours';` (the relay no longer selects them; delete or inspect by hand).
- Reminder run. If `TripReminderRuns` has no finished row for today after 12:00 IST, `TripReminderService` logs Critical `trip.reminders.missed` (EventId 7002) once that day. Check: `SELECT * FROM "TripReminderRuns" WHERE "RunDate" = (now() AT TIME ZONE 'Asia/Kolkata')::date;` (expect `FinishedAt` set).
- Final pushes look back 2 days only; the daily pass in `TripTimerService` stamps older unannounced rows (`FinalPushedAt`) and the purge removes Sent shortlist rows after 30 days.
- Not measured: the new `NOT EXISTS` probes on `Messages` and `TripOffers` and the step-less Final rows (not served by the StepKey-bound `ix_tripenquiries_final`) were run for correctness on a scratch PostgreSQL 18.3 schema, not with `EXPLAIN ANALYZE` at 400,000 rows.

## Taxi request system: launch gates and load test plan (written 2026-11-18, NOTHING MEASURED)

Read this first: no number in this section was measured. Every figure below is a target the system was designed for, copied from `F:/Dhasu/TRIP_TAXI_ARCHITECTURE.md` section 11. In the run that wrote it, no load test, no integration test against Postgres, no FCM delivery and no device test was executed: there is no PostGIS on the dev machine (`F:/Dhasu/TaxiRequest_resume/evidence/preflight_testdb.md`) and `F:/Dhasu/TaxiRequest_resume/MASTER_PLAN.md` section 14 lists these as NOT RUN. The measured tables above are about the Trip enquiry tables and do not cover this feature. Measured results go in the empty table at the end of this section, by whoever runs the plan, with the date and the machine.

### Targets (designed for, not measured) [TRIP_TAXI_ARCHITECTURE section 11]

| Target | Value |
|---|---|
| Dispatch transaction, database time | p95 under 100 ms |
| Accept write | p95 under 150 ms |
| Outbox enqueue to FCM send | p95 under 2 s |
| Push to ring on the device (read from the ack endpoint) | p95 under 5 s |
| Timer lag (due to fired) | p95 under 3 s |
| Duplicate offers and double bookings in the concurrency tests | zero |
| Fairness simulation | each driver's share of offers within 5 percent of its weight share, no driver at 0 |
| Peak assumption for the load test | about 1.2 requests per second and 10 pushes per second, then 10 times that |

### Launch gates (the master switch stays off until every item is done and recorded) [ARCHITECTURE section 11; MASTER_PLAN section 15]

1. PostGIS installed on a test machine and every `[IntegrationFact]` of the feature run and green (matcher under parallel passes, confirm race, due action claim, plan lock race, the two old/new guards, API integration). Command in `RentNearBy.Tests/README.md`. Status: open.
2. The load test plan below run against a staging copy of the server shape (Postgres, Redis, RabbitMQ and the existing background services running) and every target above met. Status: open.
3. The device test of step 5 below done, including the missed rings rule on real phones (`MASTER_PLAN.md` section 14). Status: open.
4. Owner answers to launch gates L1 to L7 (`F:/Dhasu/TaxiRequest_resume/OWNER_GATES.md`, every line OPEN, none decided): route city keys, double booking method, stay lock, unbooked request closing, Name your trip placement, planner numbers and words, old-ask dialog. Status: open.
5. Server database dropped and recreated for the regenerated `InitialCreate` and the update gate set (`CLAUDE.md`, "Deploy after a database reset"). Status: open.
6. Pilot with one hub and a small set of real drivers who finished the permission onboarding, switch off elsewhere; watch the metrics for a week and compare with the targets; widen hub by hub; rollback is the switch (requests show paused, plans still save). [ARCHITECTURE section 13]

### Load test plan (text only; run only when the owner asks) [ARCHITECTURE section 11]

1. Unit tests: state transitions, fair queue ordering, floor rule, accept cap, rating maths (`RentNearBy.Tests/TaxiRequests/`; they run in the normal suite and have run in the phase gates, `evidence/B2.md`).
2. Simulation test: 20 drivers with different weights, 1,000 requests, one driver offline for a window and back, one new driver mid-run (`TaxiFairQueueSimulationTests.cs` is the in-memory form; weights are all 1 in release 1, `TaxiDispatchRules.FairQueueStride`).
3. Integration tests against real Postgres: concurrent dispatch (no shared driver), concurrent select (one booking wins), timer re-validation, crash and restart of the worker, due action claim by two workers. The exclusion constraint of architecture 16.1 is not part of this build (gate L2).
4. Load test: ramp to the peak assumption, then 10 times it, measuring the targets above, with Postgres, Redis, RabbitMQ and the existing hosted services running (including `TaxiMatcherService` and `DueActionsWorker`). Read the numbers from the meter `RentNearBy.TaxiRequests` (`taxi.dispatch.duration`, `taxi.due_actions.lag`, `taxi.push.ack_delay`, `taxi.push.ring_sent`, `taxi.push.ring_acked`) and from `GET /api/v1/admin/taxi-requests/health` (AD5). [MASTER_PLAN 2.9, B3-6]
5. Device test: a short list of real Android phones, at least two with aggressive battery management, app closed, screen locked; measure push-to-ring delay and whether the ring sounds; test ring from the Get ready screen. [ARCHITECTURE section 11; RESEARCH_R3 section 6 in `F:/Dhasu/Analysis_2026_11_18/`]

### Alerts to wire (not measured, not yet verified in production) [MASTER_PLAN 2.9]

Log event 7110 `taxi.due.parked`, 7111 `taxi.due.lag`, 7120 `taxi.match.pass_failed`, 7121 `taxi.request.stuck`, 7130 `taxi.push.ack_low` (hourly ack ratio under 0.9, computed by the AD5 health query). Worker liveness: `/health/ready` checks the `dueActions` and `matcher` heartbeats at 30 s (work package B3-7).

### Results (empty: nothing has been measured)

| Date | Machine and data size | Target | Measured | Pass |
|---|---|---|---|---|
| none | none | none | none | none |

## Single API instance rule (FB-64, D-26)

The API runs as ONE instance. These caches live in the process memory, so a second instance would serve stale values until
their TTL ends:

| Cache | Where | Invalidation today |
|---|---|---|
| App tabs snapshot `config_app_tabs` | `AppTabCache` | write-through on the admin PUT, this process only |
| Plan catalog `plan_catalog` | `PlanCatalogCache` | version-guarded, admin writes in this process; also feeds the stay quotation monthly cap (`StayQuotationsPerMonth`), so on more than one instance an admin change of that key reaches the others only after the TTL |
| Payment switch `config_payment_feature` (now also the announce date and the switch-on time) | `PaymentFeatureReader` | `Invalidate()` on the admin PUT |
| Trip bookings switch `config_trip_execution` | `TripExecutionFeatureReader` | write-through on the admin PUT |
| Festival cache, roles, stay registration types | `FestivalCache`, `config_roles`, `stay_registration_types` | admin writes in this process |
| Owner announce quiet passes | `OwnerPlansAnnounceJob` (static) | a new announce date is a new key; one hour otherwise |
| Plan lifecycle reconcile day | `PlanLifecycleService` | the payment switch signal |

Before a scale-out: move each of these to a shared Redis version key (bump on write, compare on read) or a Redis value, and
run the hosted services (`PlanLifecycleService`, `PlanPurchaseReconcilerService`, `TripTimerService`, the matcher, the owner
jobs) on one instance only (leader lock or a separate worker deployment). Until then keep `replicas: 1` in Coolify.
