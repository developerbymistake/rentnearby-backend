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

    private static readonly RouteSeed NtlDelhi = new("Nainital", 29.3919, 79.4542, "Delhi", 28.6139, 77.2090, 6500, "OneWay");
    private static readonly RouteSeed NtlKath = new("Nainital", 29.3919, 79.4542, "Kathgodam Railway Station", 29.2683, 79.5328, 1200, "OneWay");
    private static readonly RouteSeed KathNtl = new("Kathgodam Railway Station", 29.2683, 79.5328, "Nainital", 29.3919, 79.4542, 1200, "OneWay");
    private static readonly RouteSeed NtlPant = new("Nainital", 29.3919, 79.4542, "Pantnagar Airport", 29.0340, 79.4730, 2200, "OneWay");
    private static readonly RouteSeed NtlCorbett = new("Nainital", 29.3919, 79.4542, "Jim Corbett National Park", 29.5300, 78.9500, 3500, "OneWay");
    private static readonly RouteSeed NtlRanikhet = new("Nainital", 29.3919, 79.4542, "Ranikhet", 29.6434, 79.4322, 2800, "OneWay");
    private static readonly RouteSeed NtlAlmora = new("Nainital", 29.3919, 79.4542, "Almora", 29.5971, 79.6591, 3200, "OneWay");
    private static readonly RouteSeed NtlMukt = new("Nainital", 29.3919, 79.4542, "Mukteshwar", 29.4720, 79.6490, 2600, "RoundTrip");
    private static readonly RouteSeed NtlBhimtal = new("Nainital", 29.3919, 79.4542, "Bhimtal", 29.3470, 79.5640, 900, "RoundTrip");
    private static readonly RouteSeed HaldNtl = new("Haldwani", 29.2183, 79.5130, "Nainital", 29.3919, 79.4542, 1500, "OneWay");
    private static readonly RouteSeed DelhiNtl = new("Delhi", 28.6139, 77.2090, "Nainital", 29.3919, 79.4542, 6800, "OneWay");
    private static readonly RouteSeed NtlKausani = new("Nainital", 29.3919, 79.4542, "Kausani", 29.8430, 79.6040, 4500, "OneWay");

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
        new("Mohan Bisht", "Sedan", 4, "UK04TA1001", "Nainital", 29.3919, 79.4542, 2800, 80, [NtlDelhi, NtlKath, NtlPant, NtlBhimtal]),
        new("Girish Rawat", "SUV", 6, "UK04TA1002", "Nainital", 29.3880, 79.4600, 3800, 100, [NtlCorbett, NtlRanikhet, NtlAlmora, KathNtl]),
        new("Pradeep Joshi", "Hatchback", 4, "UK04TA1003", "Haldwani", 29.2183, 79.5130, 2200, 70, [HaldNtl, NtlKath, KathNtl, NtlPant]),
        new("Harish Negi", "Sedan", 4, "UK04TA1004", "Bhimtal", 29.3470, 79.5640, 2600, 80, [NtlBhimtal, NtlMukt, NtlKath]),
        new("Sunil Kumar", "MUV", 7, "UK04TA1005", "Kathgodam", 29.2683, 79.5328, 4200, 120, [KathNtl, NtlDelhi, NtlCorbett, NtlRanikhet]),
        new("Mahesh Adhikari", "TempoTraveller", 12, "UK04TA1006", "Haldwani", 29.2183, 79.5130, 7500, 150, [DelhiNtl, NtlDelhi, NtlCorbett, NtlKausani]),
        new("Rajesh Pant", "SUV", 6, "UK04TA1007", "Nainital", 29.3950, 79.4500, 3600, 100, [NtlMukt, NtlAlmora, NtlKausani, NtlKath]),
        new("Dinesh Mehra", "Sedan", 4, "UK04TA1008", "Bhowali", 29.3870, 79.5100, 2500, 80, [NtlKath, NtlPant, NtlBhimtal, NtlRanikhet]),
        new("Naveen Tiwari", "Hatchback", 4, "UK04TA1009", "Nainital", 29.3850, 79.4560, 2000, 60, [NtlBhimtal, KathNtl, NtlKath]),
        new("Yogesh Kandpal", "MUV", 7, "UK04TA1010", "Ramnagar", 29.3950, 79.1280, 4000, 120, [NtlCorbett, NtlDelhi, KathNtl, NtlKausani]),
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
            for (var p = 0; p < 4; p++)
            {
                stay.Photos.Add(new StayPhoto
                {
                    Id = Guid.NewGuid(), StayListingId = stayId, PhotoOrder = p,
                    PhotoUrl = $"https://picsum.photos/seed/demo-stay-{i + 1}-{p + 1}/900/600",
                    FilePath = $"demo/stay-{i + 1}-{p + 1}", UploadedAt = now,
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
            if (await db.Users.AnyAsync(u => u.PhoneNumber == phone)) continue;

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
                PlatePhotoUrl = $"https://picsum.photos/seed/demo-plate-{i + 1}/800/500",
                PlatePhotoPath = $"demo/plate-{i + 1}",
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

        if (stayCount + taxiCount == 0) { logger.LogInformation("demo.seed.nainital: already present"); return; }
        await db.SaveChangesAsync();
        logger.LogInformation("demo.seed.nainital: added {Stays} stays and {Taxis} taxis", stayCount, taxiCount);
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
