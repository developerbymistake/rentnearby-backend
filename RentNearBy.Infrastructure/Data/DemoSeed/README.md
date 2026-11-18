# Nainital demo seed (test data only)

Adds 10 approved stays and 10 approved taxi drivers in Nainital district (users with fake phones 90000001xx / 90000002xx)
so a seeker can test the Trip Planner. Owners cannot log in.

Runs on API start only when `DemoSeedGate.ShouldRun` says so: always in Development; in any other environment only with
`DemoSeed:Enabled=true` (env `DemoSeed__Enabled`); never in Production, even when enabled. Idempotent: existing demo phones are skipped.

Remove completely: delete this folder, delete the `NainitalDemoSeeder.SeedAsync` statement in `RentNearBy.Api/Program.cs`,
run `remove_demo_seed.sql` on the database, flush Redis stay/taxi caches.
