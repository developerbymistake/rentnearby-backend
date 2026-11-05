# Nainital demo seed (test data only)

Adds 10 approved stays and 10 approved taxi drivers in Nainital district (users with fake phones 90000001xx / 90000002xx)
so a seeker can test the Trip Planner. Owners cannot log in.

Runs automatically on every API start, no env needed (idempotent: existing demo phones are skipped).

Remove completely: delete this folder, delete the `NainitalDemoSeeder.SeedAsync` statement in `RentNearBy.Api/Program.cs`,
run `remove_demo_seed.sql` on the database, flush Redis stay/taxi caches.
