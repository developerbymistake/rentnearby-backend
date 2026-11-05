-- Removes every demo user from NainitalDemoSeeder (phones 90000001xx stay owners, 90000002xx taxi drivers).
-- Their stays, rooms, photos, taxis and routes cascade. Trip enquiries/leads sent to them are closed by the
-- normal listing-removal path only when done through the app; if testers asked them, delete those rows first:
DELETE FROM "TripEnquiries" WHERE "ListingId" IN (
  SELECT "Id" FROM "StayListings" WHERE "UserId" IN (SELECT "Id" FROM "Users" WHERE "PhoneNumber" ~ '^900000(01|02)[0-9]{2}$')
  UNION
  SELECT "Id" FROM "TaxiListings" WHERE "UserId" IN (SELECT "Id" FROM "Users" WHERE "PhoneNumber" ~ '^900000(01|02)[0-9]{2}$'));
DELETE FROM "Users" WHERE "PhoneNumber" ~ '^900000(01|02)[0-9]{2}$';
-- Then flush Redis keys for stays/taxis browse (or wait for the cache TTL).
