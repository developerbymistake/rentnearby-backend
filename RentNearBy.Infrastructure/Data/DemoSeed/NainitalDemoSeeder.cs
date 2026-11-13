using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Core.Utils;

namespace RentNearBy.Infrastructure.Data.DemoSeed;

// TEST DATA ONLY. 10 approved Stay owners and 10 approved Taxi drivers in Nainital district so a seeker can test the
// Trip Planner. Runs on every start. Owners cannot log in (fake phones, no OTP).
// Remove: delete this folder + the one SeedAsync statement in Program.cs + run remove_demo_seed.sql.
public static class NainitalDemoSeeder
{
    // Marker for cleanup: every demo user has a phone 90000001xx (stay) or 90000002xx (taxi).
    private const string StayPhonePrefix = "90000001";
    private const string TaxiPhonePrefix = "90000002";

    private sealed record RoomSeed(string Name, int Price, short Guests, string Meals, short? Floor);
    private sealed record StaySeed(
        string Name, string Owner, string Type, string City, double Lat, double Lng, string Address, string Access,
        bool HostLives, string[] Facilities, string Description, RoomSeed[] Rooms, bool Pets, bool Couples);
    private sealed record RouteSeed(string From, double FromLat, double FromLng, string To, double ToLat, double ToLng, int Fare, string Type);
    private sealed record TaxiSeed(
        string Driver, string Category, short Seats, string Plate, string City, double Lat, double Lng,
        int? DayRate, int? DayKm, RouteSeed[] Routes);

    // Places across Uttarakhand so trips of several cities find a car for every leg.
    private static readonly Dictionary<string, (double Lat, double Lng)> Places = new()
    {
        ["Nainital"] = (29.3919, 79.4542), ["Bhimtal"] = (29.3470, 79.5640), ["Mukteshwar"] = (29.4720, 79.6490),
        ["Kathgodam Railway Station"] = (29.2683, 79.5328), ["Haldwani"] = (29.2183, 79.5130), ["Pantnagar Airport"] = (29.0340, 79.4730),
        ["Ramnagar"] = (29.3950, 79.1280), ["Jim Corbett National Park"] = (29.5390, 79.1340),
        ["Almora"] = (29.5971, 79.6591), ["Ranikhet"] = (29.6434, 79.4322), ["Kausani"] = (29.8430, 79.6040), ["Bageshwar"] = (29.8379, 79.7710),
        ["Delhi"] = (28.6139, 77.2090), ["Haridwar"] = (29.9457, 78.1642), ["Rishikesh"] = (30.0869, 78.2676),
        ["Dehradun"] = (30.3165, 78.0322), ["Mussoorie"] = (30.4598, 78.0664), ["Badrinath"] = (30.7433, 79.4938),
    };

    private static RouteSeed R(string from, string to, int fare, string type = "OneWay")
    {
        var f = Places[from];
        var d = Places[to];
        return new RouteSeed(from, f.Lat, f.Lng, to, d.Lat, d.Lng, fare, type);
    }

    private static readonly StaySeed[] Stays =
    [
        new("Naini Lake View Homestay", "Rakesh Bisht", "Homestay", "Nainital", 29.3919, 79.4542, "Mallital, Nainital", StayVehicleAccess.CarToGate, true,
            [StayFacilities.HotWater, StayFacilities.Heater, StayFacilities.HomeFood, StayFacilities.MountainView],
            "Family run homestay close to the Mall Road with a lake view from the balcony and home cooked food.",
            [new("Standard", 1800, 2, StayMeals.Breakfast, 1), new("Family Room", 3200, 4, StayMeals.AllMeals, 0)], false, false),
        new("Deodar Heights Cottage", "Hema Joshi", "Cottage", "Nainital", 29.3800, 79.4600, "Tallital, Nainital", StayVehicleAccess.ShortWalk, true,
            [StayFacilities.HotWater, StayFacilities.Heater, StayFacilities.WiFi, StayFacilities.Parking],
            "Quiet cottage among deodar trees, ten minutes from the lake.",
            [new("Deluxe", 2500, 3, StayMeals.RoomOnly, 0), new("Suite", 4200, 4, StayMeals.Breakfast, 1)], true, true),
        new("Bhimtal Lakeside Resort", "Sunil Negi", "Resort", "Bhimtal", 29.3470, 79.5640, "Bhimtal Lake Road", StayVehicleAccess.CarToGate, false,
            [StayFacilities.HotWater, StayFacilities.WiFi, StayFacilities.Parking, StayFacilities.MountainView],
            "Lakeside rooms with a garden, parking for six cars and a restaurant.",
            [new("Premium", 5200, 3, StayMeals.Breakfast, 0), new("Family Room", 6800, 4, StayMeals.AllMeals, 0)], true, true),
        new("Sattal Forest Villa", "Deepak Rawat", "Villa", "Bhimtal", 29.3550, 79.5460, "Sattal Road, Bhimtal", StayVehicleAccess.CarToGate, false,
            [StayFacilities.HotWater, StayFacilities.Heater, StayFacilities.WiFi, StayFacilities.Parking, StayFacilities.HomeFood],
            "Whole villa feel near the Sattal lakes, good for groups and families.",
            [new("Executive", 7500, 4, StayMeals.AllMeals, 0), new("Suite", 9200, 4, StayMeals.AllMeals, 1)], true, true),
        new("Pine Breeze Farm Stay", "Mahesh Pant", "Farm stay", "Bhowali", 29.3870, 79.5100, "Bhowali, Nainital", StayVehicleAccess.CarToGate, true,
            [StayFacilities.HotWater, StayFacilities.HomeFood, StayFacilities.Parking],
            "Fruit orchard stay with home food, a short drive from Nainital.",
            [new("Standard", 1500, 2, StayMeals.AllMeals, 0), new("Deluxe", 2200, 3, StayMeals.AllMeals, 0)], true, false),
        new("Mukteshwar Himalayan Retreat", "Anita Kandpal", "Resort", "Mukteshwar", 29.4720, 79.6490, "Mukteshwar, Ramgarh Block", StayVehicleAccess.CarToGate, false,
            [StayFacilities.HotWater, StayFacilities.Heater, StayFacilities.WiFi, StayFacilities.MountainView],
            "Snow view rooms above the clouds, quiet and cold, heater in every room.",
            [new("Deluxe", 3800, 3, StayMeals.Breakfast, 0), new("Premium", 5600, 4, StayMeals.AllMeals, 1)], false, true),
        new("Naukuchiatal Boat House", "Prakash Adhikari", "Homestay", "Bhimtal", 29.3190, 79.5580, "Naukuchiatal", StayVehicleAccess.ShortWalk, true,
            [StayFacilities.HotWater, StayFacilities.HomeFood, StayFacilities.MountainView],
            "Simple rooms next to the nine corner lake, owner cooks on request.",
            [new("Standard", 1400, 2, StayMeals.Breakfast, 0), new("Family Room", 2600, 4, StayMeals.AllMeals, 1)], false, false),
        new("Kathgodam Station Inn", "Vikas Tiwari", "Homestay", "Kathgodam", 29.2683, 79.5328, "Near Kathgodam Railway Station", StayVehicleAccess.CarToGate, false,
            [StayFacilities.HotWater, StayFacilities.WiFi, StayFacilities.Parking],
            "Clean budget rooms five minutes from the station, good for an early train.",
            [new("Standard", 1100, 2, StayMeals.RoomOnly, 0), new("Deluxe", 1700, 3, StayMeals.Breakfast, 1)], true, true),
        new("Ramgarh Orchard Villa", "Lalita Mehra", "Villa", "Ramgarh", 29.4180, 79.5560, "Ramgarh, Nainital", StayVehicleAccess.CarToGate, true,
            [StayFacilities.HotWater, StayFacilities.Heater, StayFacilities.HomeFood, StayFacilities.Parking, StayFacilities.MountainView],
            "Orchard villa in the fruit belt, ground floor room for elders.",
            [new("Deluxe", 3000, 3, StayMeals.AllMeals, 0), new("Family Room", 4800, 4, StayMeals.AllMeals, 0)], true, false),
        new("Snow View Mall Road Hotel", "Rohit Sah", "Resort", "Nainital", 29.3990, 79.4510, "Mall Road, Mallital", StayVehicleAccess.LongWalk, false,
            [StayFacilities.HotWater, StayFacilities.Heater, StayFacilities.WiFi],
            "Right on the Mall Road, walk to the boat club and market, no car access.",
            [new("Standard", 2800, 2, StayMeals.Breakfast, 2), new("Premium", 4500, 4, StayMeals.Breakfast, 3)], false, true),
    ];

    private static readonly TaxiSeed[] Taxis =
    [
        new("Mohan Bisht", "Sedan", 4, "UK04TA1001", "Nainital", 29.3919, 79.4542, 2800, 80,
            [R("Nainital", "Delhi", 6500), R("Delhi", "Nainital", 6800), R("Nainital", "Haridwar", 5500), R("Haridwar", "Rishikesh", 1500), R("Nainital", "Kathgodam Railway Station", 1200), R("Nainital", "Pantnagar Airport", 2200)]),
        new("Girish Rawat", "SUV", 6, "UK04TA1002", "Nainital", 29.3880, 79.4600, 3800, 100,
            [R("Nainital", "Almora", 3200), R("Almora", "Kausani", 2800), R("Kausani", "Ranikhet", 2600), R("Ranikhet", "Nainital", 2800), R("Nainital", "Jim Corbett National Park", 3500), R("Jim Corbett National Park", "Ramnagar", 1500)]),
        new("Pradeep Joshi", "Hatchback", 4, "UK04TA1003", "Haldwani", 29.2183, 79.5130, 2200, 70,
            [R("Haldwani", "Nainital", 1500), R("Haldwani", "Delhi", 5500), R("Kathgodam Railway Station", "Almora", 3000), R("Haldwani", "Ramnagar", 1800), R("Haldwani", "Rishikesh", 6500), R("Pantnagar Airport", "Haldwani", 900)]),
        new("Harish Negi", "Sedan", 4, "UK04TA1004", "Bhimtal", 29.3470, 79.5640, 2600, 80,
            [R("Bhimtal", "Nainital", 900, "RoundTrip"), R("Nainital", "Mukteshwar", 2600, "RoundTrip"), R("Mukteshwar", "Almora", 2800), R("Bhimtal", "Kathgodam Railway Station", 1300), R("Mukteshwar", "Kathgodam Railway Station", 3000)]),
        new("Sunil Kumar", "MUV", 7, "UK04TA1005", "Kathgodam", 29.2683, 79.5328, 4200, 120,
            [R("Kathgodam Railway Station", "Nainital", 1200), R("Kathgodam Railway Station", "Delhi", 6200), R("Delhi", "Kathgodam Railway Station", 6200), R("Kathgodam Railway Station", "Haridwar", 5200), R("Haridwar", "Dehradun", 2200), R("Dehradun", "Mussoorie", 1800)]),
        new("Mahesh Adhikari", "TempoTraveller", 12, "UK04TA1006", "Haldwani", 29.2183, 79.5130, 7500, 150,
            [R("Delhi", "Nainital", 9500), R("Nainital", "Delhi", 9500), R("Delhi", "Haridwar", 7000), R("Haridwar", "Rishikesh", 2500), R("Rishikesh", "Badrinath", 16000), R("Haridwar", "Mussoorie", 4800)]),
        new("Rajesh Pant", "SUV", 6, "UK04TA1007", "Nainital", 29.3950, 79.4500, 3600, 100,
            [R("Nainital", "Kausani", 4500), R("Kausani", "Bageshwar", 2200), R("Bageshwar", "Almora", 3000), R("Almora", "Nainital", 3200), R("Nainital", "Mukteshwar", 2600), R("Ranikhet", "Almora", 1800)]),
        new("Dinesh Mehra", "Sedan", 4, "UK04TA1008", "Bhowali", 29.3870, 79.5100, 2500, 80,
            [R("Nainital", "Ranikhet", 2800), R("Ranikhet", "Kausani", 2800), R("Nainital", "Kathgodam Railway Station", 1300), R("Nainital", "Almora", 2600), R("Nainital", "Bhimtal", 900, "RoundTrip")]),
        new("Naveen Tiwari", "Hatchback", 4, "UK04TA1009", "Nainital", 29.3850, 79.4560, 2000, 60,
            [R("Nainital", "Bhimtal", 900, "RoundTrip"), R("Bhimtal", "Kathgodam Railway Station", 1300), R("Kathgodam Railway Station", "Nainital", 1200), R("Nainital", "Ranikhet", 2800), R("Mukteshwar", "Nainital", 2600)]),
        new("Yogesh Kandpal", "MUV", 7, "UK04TA1010", "Ramnagar", 29.3950, 79.1280, 4000, 120,
            [R("Ramnagar", "Jim Corbett National Park", 1200), R("Jim Corbett National Park", "Nainital", 3500), R("Nainital", "Ramnagar", 3000), R("Ramnagar", "Delhi", 5200), R("Delhi", "Ramnagar", 5400), R("Ramnagar", "Haridwar", 4500)]),
    ];

    public static async Task SeedAsync(ApplicationDbContext db, IIdentityCipher cipher, ILogger logger)
    {
        var district = await db.Districts.FirstOrDefaultAsync(d => d.Name == "Nainital" && d.StateName == "Uttarakhand");
        if (district == null) { logger.LogWarning("demo.seed.skipped: Nainital district not found"); return; }

        var cities = await db.Cities.Where(c => c.DistrictId == district.Id).ToListAsync();
        Guid? CityId(string name) => cities.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))?.Id;

        var stayTypes = await db.StayTypes.ToListAsync();
        var regTypes = await db.StayRegistrationTypes.Where(r => r.IsActive).OrderBy(r => r.SortOrder).ToListAsync();
        var regType = regTypes.FirstOrDefault(r => r.Code == "UDYAM") ?? regTypes.FirstOrDefault();
        var vehicleTypes = await db.TaxiVehicleTypes.Where(v => v.IsActive).OrderBy(v => v.SortOrder).ToListAsync();
        if (regType == null || stayTypes.Count == 0 || vehicleTypes.Count == 0)
        {
            logger.LogWarning("demo.seed.skipped: lookups missing");
            return;
        }

        var now = DateTime.UtcNow;
        var stayCount = 0;
        var taxiCount = 0;
        var rewritten = 0;

        // Earlier versions pointed demo photos at picsum.photos, which is not our storage. Real photos live in Cloudinary.
        var fakeStayPhotos = await db.StayPhotos.Where(p => p.PhotoUrl.StartsWith("https://picsum.photos/")).ToListAsync();
        db.StayPhotos.RemoveRange(fakeStayPhotos);
        var fakePlates = await db.TaxiListings.Where(t => t.PlatePhotoUrl != null && t.PlatePhotoUrl.StartsWith("https://picsum.photos/")).ToListAsync();
        foreach (var t in fakePlates) { t.PlatePhotoUrl = null; t.PlatePhotoPath = null; }
        rewritten += fakeStayPhotos.Count + fakePlates.Count;

        for (var i = 0; i < Stays.Length; i++)
        {
            var s = Stays[i];
            var phone = $"{StayPhonePrefix}{i + 1:00}";
            if (await db.Users.AnyAsync(u => u.PhoneNumber == phone)) continue;

            var type = stayTypes.FirstOrDefault(t => string.Equals(t.Name, s.Type, StringComparison.OrdinalIgnoreCase)) ?? stayTypes[0];
            var user = NewUser(phone, s.Owner, UserRoles.StayOwnerId, now);
            var stayId = Guid.NewGuid();
            var stay = new StayListing
            {
                Id = stayId,
                UserId = user.Id,
                Name = s.Name,
                StayTypeId = type.Id,
                VehicleAccess = s.Access,
                HostLivesHere = s.HostLives,
                Facilities = s.Facilities,
                Description = s.Description,
                RegistrationTypeId = regType.Id,
                RegistrationTypeName = regType.Name,
                RegistrationOtherName = regType.RequiresOtherName ? "Demo trade licence" : null,
                RegistrationNumber = regType.Code == "UDYAM" ? $"UDYAM-UK-04-{i + 1:0000000}" : $"DEMO{i + 1:0000}",
                Gstin = DemoGstin(i),
                Latitude = (decimal)s.Lat,
                Longitude = (decimal)s.Lng,
                Address = s.Address,
                DistrictId = district.Id,
                CityId = CityId(s.City),
                CheckInTime = new TimeOnly(12, 0),
                CheckOutTime = new TimeOnly(11, 0),
                PetsAllowed = s.Pets,
                SmokingAllowed = false,
                AlcoholAllowed = false,
                UnmarriedCouplesAllowed = s.Couples,
                IdProofRequired = true,
                CancellationPolicy = StayCancellation.Flexible,
                RulesSavedAt = now,
                Status = StayStatuses.Approved,
                EverApproved = true,
                AcceptingGuests = true,
                SubmittedAt = now,
                ReviewedAt = now,
                MinPricePerNight = s.Rooms.Min(r => r.Price),
                Slug = $"{SlugGenerator.Generate($"{s.Name} {s.City}")}-demo{i + 1}",
                UpiId = $"demostay{i + 1}@upi",
                WhatsAppNumber = phone,
                CreatedAt = now.AddMinutes(-i),
                UpdatedAt = now,
            };
            for (var r = 0; r < s.Rooms.Length; r++)
            {
                var room = s.Rooms[r];
                stay.Rooms.Add(new StayRoom
                {
                    Id = Guid.NewGuid(), StayListingId = stayId, RoomIndex = (short)(r + 1), PricePerNight = room.Price,
                    MaxGuests = room.Guests, RoomName = room.Name, Meals = room.Meals, Floor = room.Floor,
                    CreatedAt = now, UpdatedAt = now,
                });
            }
            db.Users.Add(user);
            db.StayListings.Add(stay);
            stayCount++;
        }

        for (var i = 0; i < Taxis.Length; i++)
        {
            var t = Taxis[i];
            var phone = $"{TaxiPhonePrefix}{i + 1:00}";
            var existing = await db.Users.Where(u => u.PhoneNumber == phone).Select(u => u.Id).FirstOrDefaultAsync();
            if (existing != Guid.Empty)
            {
                var demoTaxi = await db.TaxiListings.Include(x => x.Routes).FirstOrDefaultAsync(x => x.UserId == existing);
                if (demoTaxi != null && RewriteRoutes(db, demoTaxi, t.Routes, now)) rewritten++;
                continue;
            }

            var vehicle = vehicleTypes.FirstOrDefault(v => v.Category == t.Category) ?? vehicleTypes[0];
            var user = NewUser(phone, t.Driver, UserRoles.TaxiDriverId, now);
            var taxiId = Guid.NewGuid();
            var dl = $"UK0720200000{i + 101}";
            var taxi = new TaxiListing
            {
                Id = taxiId,
                UserId = user.Id,
                VehicleTypeId = vehicle.Id,
                Seats = t.Seats,
                NumberPlate = TaxiPlate.Normalize(t.Plate),
                ContactPhone = phone,
                WhatsAppNumber = phone,
                DrivingLicenceNumberEnc = cipher.IsConfigured ? cipher.Protect(dl) : "demo-not-encrypted",
                DateOfBirth = new DateOnly(1985 + i % 10, 1 + i % 12, 10 + i),
                Latitude = (decimal)t.Lat,
                Longitude = (decimal)t.Lng,
                DistrictId = district.Id,
                CityId = CityId(t.City),
                Status = TaxiStatuses.Approved,
                EverApproved = true,
                IsAvailable = true,
                SubmittedAt = now,
                ReviewedAt = now,
                MinFare = t.Routes.Min(r => r.Fare),
                LocalDayRate = t.DayRate,
                LocalDayKm = t.DayKm,
                Slug = $"{SlugGenerator.Generate($"{t.Driver} taxi {t.City}")}-demo{i + 1}",
                CreatedAt = now.AddMinutes(-i),
                UpdatedAt = now,
            };
            for (var r = 0; r < t.Routes.Length; r++)
            {
                var route = t.Routes[r];
                taxi.Routes.Add(new TaxiRoute
                {
                    Id = Guid.NewGuid(), TaxiListingId = taxiId, RouteIndex = (short)(r + 1),
                    FromName = route.From, FromLatitude = (decimal)route.FromLat, FromLongitude = (decimal)route.FromLng,
                    ToName = route.To, ToLatitude = (decimal)route.ToLat, ToLongitude = (decimal)route.ToLng,
                    Fare = route.Fare, TripType = route.Type, CreatedAt = now, UpdatedAt = now,
                });
            }
            db.Users.Add(user);
            db.TaxiListings.Add(taxi);
            taxiCount++;
        }

        if (!HasChanges(stayCount, taxiCount, rewritten)) { logger.LogInformation("demo.seed.nainital: already present"); return; }
        await db.SaveChangesAsync();
        logger.LogInformation("demo.seed.nainital: added {Stays} stays and {Taxis} taxis, rewrote routes of {Rewritten} taxis",
            stayCount, taxiCount, rewritten);
    }

    internal static bool HasChanges(int stayCount, int taxiCount, int rewritten) => stayCount + taxiCount + rewritten > 0;

    internal static bool RoutesMatch(IEnumerable<TaxiRoute> stored, IReadOnlyList<(string From, string To, int Fare)> wanted)
        => stored.OrderBy(r => r.RouteIndex).Select(r => (r.FromName, r.ToName, r.Fare)).SequenceEqual(wanted);

    // Re-runs bring an already seeded database to the current route list. Only tracks the delete and the inserts:
    // the caller's single SaveChanges commits both together (EF orders the deletes first for the unique RouteIndex).
    private static bool RewriteRoutes(ApplicationDbContext db, TaxiListing taxi, RouteSeed[] routes, DateTime now)
    {
        if (RoutesMatch(taxi.Routes, routes.Select(r => (r.From, r.To, r.Fare)).ToList())) return false;
        db.TaxiRoutes.RemoveRange(taxi.Routes);
        for (var r = 0; r < routes.Length; r++)
        {
            var route = routes[r];
            db.TaxiRoutes.Add(new TaxiRoute
            {
                Id = Guid.NewGuid(), TaxiListingId = taxi.Id, RouteIndex = (short)(r + 1),
                FromName = route.From, FromLatitude = (decimal)route.FromLat, FromLongitude = (decimal)route.FromLng,
                ToName = route.To, ToLatitude = (decimal)route.ToLat, ToLongitude = (decimal)route.ToLng,
                Fare = route.Fare, TripType = route.Type, CreatedAt = now, UpdatedAt = now,
            });
        }
        taxi.MinFare = routes.Min(r => r.Fare);
        taxi.UpdatedAt = now;
        return true;
    }

    // Valid GSTIN shape and check character (state 05 = Uttarakhand), so admin and owner screens show a real looking value.
    private static string DemoGstin(int i)
    {
        var first14 = $"05ABCD{(char)('A' + i)}{1000 + i}F1Z";
        return first14 + StayRegistrationRules.GstinCheckChar(first14);
    }

    private static User NewUser(string phone, string name, Guid roleId, DateTime now) => new()
    {
        Id = Guid.NewGuid(),
        PhoneNumber = phone,
        Name = name,
        IsActive = true,
        IsPhoneVerified = true,
        IsContactVisible = true,
        RoleId = roleId,
        RoleConfirmedAt = now,
        CreatedAt = now,
        UpdatedAt = now,
    };
}
