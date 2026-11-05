# Nainital demo seed (test data only)

Adds 10 approved stays and 10 approved taxi drivers in Nainital district (users with fake phones 90000001xx / 90000002xx)
so a seeker can test the Trip Planner. Owners cannot log in.

Turn on: set env `DEMO_SEED_NAINITAL=true` and restart the API (idempotent: existing demo phones are skipped).
Turn off: unset it (nothing is added; existing demo rows stay until removed).

Remove completely: delete this folder, delete the `DEMO_SEED_NAINITAL` block in `RentNearBy.Api/Program.cs`,
run `remove_demo_seed.sql` on the database, flush Redis stay/taxi caches.
