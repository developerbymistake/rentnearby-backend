using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Models;
using RentNearBy.Core.Utils;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RentNearBy.Infrastructure.Data;

public static class DataSeeder
{
    private sealed record DistrictSeedRecord(string Name, string StateName, string Boundary);
    internal sealed record CitySeedRecord(string Name, string DistrictName, double Latitude, double Longitude);
    internal sealed record CitySeedRow(long CityKey, string Name, string DistrictName, double Latitude, double Longitude, string SeedStatus);

    private const string SeedStatusOk = "Ok";

    private static readonly JsonSerializerOptions CaseInsensitiveJson = new() { PropertyNameCaseInsensitive = true };
    private static readonly WKTReader WktReader = new();

    public static async Task SeedAsync(ApplicationDbContext db, ILogger logger)
    {
        await SeedRoomTypesAsync(db);
        await SeedPlotTypesAsync(db);
        await SeedStayTypesAsync(db);
        await SeedStayRegistrationTypesAsync(db);
        await SeedStayJobLookupsAsync(db);
        await SeedTaxiVehicleTypesAsync(db);
        await SeedReportReasonsAsync(db);
        await SeedQuestionTemplatesAsync(db);
        await SeedPlansAsync(db);
        await SeedDistrictsAsync(db);
        await SeedCitiesAsync(db, logger);
        await RecomputeBighaAreasAsync(db, logger);
        await SeedAppFeatureFlagsAsync(db);
        await SeedAppTabsAsync(db);
        await SeedAppVersionConfigsAsync(db);
        await SeedAdminsAsync(db);
    }

    // Payment kill switch — starts OFF (payment not required) so Go-Live is free by default until an
    // admin explicitly turns payment back on. Do not "fix" IsEnabled to true here; that flip is an
    // explicit product decision, not an oversight.
    private static async Task SeedAppFeatureFlagsAsync(ApplicationDbContext db)
    {
        if (await db.AppFeatureFlags.AnyAsync()) return;

        db.AppFeatureFlags.Add(new AppFeatureFlag
        {
            Id = Guid.NewGuid(),
            FeatureKey = AppFeatureKeys.PaymentEnabled,
            IsEnabled = false,
            FreeDurationDays = 30,
            PaymentProvider = PaymentProviders.Play,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    // Master table for the consumer app's 7 bottom-nav tabs. Inserts only missing keys, so tabs added
    // later reach old databases. Existing rows only get their SortOrder realigned (not admin-editable);
    // admin renames/toggles are never touched.
    // Home/Profile stay active forever (AdminHandlers.UpdateAppTab refuses to deactivate them, see
    // AppTabKeys.NonDeactivatable).
    // Rooms and Plots are seeded OFF on a fresh database (being phased out); an admin turns them on in the
    // App Tabs screen. Rows that already exist keep whatever the admin set.
    private static readonly HashSet<string> TabsSeededOff = [AppTabKeys.Rooms, AppTabKeys.Plots];

    private static async Task SeedAppTabsAsync(ApplicationDbContext db)
    {
        var tabs = new (string Key, string DisplayName, int SortOrder)[]
        {
            (AppTabKeys.Home, "Home", 0),
            (AppTabKeys.Rooms, "Rooms", 1),
            (AppTabKeys.Plots, "Plots", 2),
            (AppTabKeys.Stays, "Stays", 3),
            (AppTabKeys.Jobs, "Jobs", 4),
            (AppTabKeys.Taxi, "Taxi", 5),
            (AppTabKeys.Profile, "Profile", 6),
        };

        var existingRows = await db.AppTabs.ToListAsync();
        var existing = existingRows.Select(t => t.TabKey).ToHashSet();
        var missing = tabs.Where(t => !existing.Contains(t.Key)).ToList();

        var changed = false;
        foreach (var row in existingRows)
        {
            var canonical = Array.FindIndex(tabs, t => t.Key == row.TabKey);
            if (canonical < 0 || row.SortOrder == tabs[canonical].SortOrder) continue;
            row.SortOrder = tabs[canonical].SortOrder;
            changed = true;
        }

        if (missing.Count == 0 && !changed) return;

        foreach (var (key, displayName, sortOrder) in missing)
        {
            db.AppTabs.Add(new AppTab
            {
                Id = Guid.NewGuid(),
                TabKey = key,
                DisplayName = displayName,
                IsActive = !TabsSeededOff.Contains(key),
                SortOrder = sortOrder,
                UpdatedAt = DateTime.UtcNow,
            });
        }
        await db.SaveChangesAsync();
    }

    // Seeded disabled with build 1 so deploying the backend never gates anyone until an admin opts in.
    private static async Task SeedAppVersionConfigsAsync(ApplicationDbContext db)
    {
        var added = false;
        foreach (var platform in AppPlatforms.All)
        {
            if (await db.AppVersionConfigs.AnyAsync(c => c.Platform == platform)) continue;

            db.AppVersionConfigs.Add(new AppVersionConfig
            {
                Id = Guid.NewGuid(),
                Platform = platform,
                IsEnabled = false,
                MinBuildNumber = 1,
                MinVersionName = "1.0.0",
                LatestBuildNumber = 1,
                LatestVersionName = "1.0.0",
                StoreUrl = platform == AppPlatforms.Android
                    ? AppPlatforms.AndroidStoreUrl
                    : null,
                ForceCacheMaxAgeHours = AppVersionRules.DefaultForceCacheMaxAgeHours,
                MaxSeenBuild = 0,
                UpdatedAt = DateTime.UtcNow,
            });
            added = true;
        }
        if (added) await db.SaveChangesAsync();
    }

    // Starting plan catalog (prices are guesses; admin-editable). Free is the default plan of each
    // feature (no prices); paid plans carry 1/3/12-month prices with a longer-cycle discount. Stable ids:
    // plans b1000000-...-0000000FFNN (FF feature 01-04, NN 01 free / 02 paid), prices b2000000-...-0000FFCC.
    private static async Task SeedPlansAsync(ApplicationDbContext db)
    {
        if (await db.Plans.AnyAsync())
        {
            await SyncSeedPlayPricesAsync(db);
            return;
        }

        var now = DateTime.UtcNow;
        var features = new (int Index, string Kind, string Code)[]
        {
            (1, ListingKinds.Room, "ROOM"),
            (2, ListingKinds.Plot, "PLOT"),
            (3, ListingKinds.Stay, "STAY"),
            (4, ListingKinds.Taxi, "TAXI"),
        };

        static Guid PlanId(int feature, int n) => new($"b1000000-0000-0000-0000-000000000{feature}{n:D2}");
        static Guid PriceId(int feature, int cycle) => new($"b2000000-0000-0000-0000-00000000{feature:D2}{cycle:D2}");

        foreach (var (index, kind, code) in features)
        {
            var isStay = kind == ListingKinds.Stay;
            var isSlots = kind is ListingKinds.Room or ListingKinds.Plot;

            var free = new Plan
            {
                Id = PlanId(index, 1),
                FeatureKey = kind,
                Code = $"{code}_FREE",
                Name = "Free",
                Tagline = "Get started",
                IsDefault = true,
                IsEnabled = true,
                TrialDays = 0,
                SortOrder = 0,
                CreatedAt = now,
                UpdatedAt = now,
            };
            free.Features.Add(new PlanFeature { Id = Guid.NewGuid(), Key = PlanFeatureKeys.ListingLimit, Value = 1 });
            free.Features.Add(new PlanFeature { Id = Guid.NewGuid(), Key = PlanFeatureKeys.Priority, Value = 0 });
            free.Features.Add(new PlanFeature { Id = Guid.NewGuid(), Key = PlanFeatureKeys.Featured, Value = 0 });
            if (isStay) free.Features.Add(new PlanFeature { Id = Guid.NewGuid(), Key = PlanFeatureKeys.JobsPosts, Value = 1 });

            var plus = new Plan
            {
                Id = PlanId(index, 2),
                FeatureKey = kind,
                Code = $"{code}_PLUS",
                Name = "Plus",
                Tagline = isSlots ? "2 listings, shown first" : "Featured and shown first",
                IsDefault = false,
                IsEnabled = true,
                TrialDays = 7,
                SortOrder = 1,
                PlayProductId = $"{code.ToLowerInvariant()}_plus",
                CreatedAt = now,
                UpdatedAt = now,
            };
            plus.Features.Add(new PlanFeature { Id = Guid.NewGuid(), Key = PlanFeatureKeys.ListingLimit, Value = isSlots ? 2 : 1 });
            plus.Features.Add(new PlanFeature { Id = Guid.NewGuid(), Key = PlanFeatureKeys.Priority, Value = 1 });
            plus.Features.Add(new PlanFeature { Id = Guid.NewGuid(), Key = PlanFeatureKeys.Featured, Value = isSlots ? 0 : 1 });
            if (isStay) plus.Features.Add(new PlanFeature { Id = Guid.NewGuid(), Key = PlanFeatureKeys.JobsPosts, Value = 4 });

            var (m1, m3, m12) = kind switch
            {
                ListingKinds.Stay => (400, 1100, 3800),
                ListingKinds.Taxi => (150, 400, 1400),
                _ => (200, 550, 1900),
            };
            plus.Prices.Add(new PlanPrice { Id = PriceId(index, 1), CycleMonths = 1, PriceInr = m1, IsEnabled = true, PlayBasePlanId = "cycle-1m", PlayTrialOfferId = "trial-7d" });
            plus.Prices.Add(new PlanPrice { Id = PriceId(index, 3), CycleMonths = 3, PriceInr = m3, IsEnabled = true, PlayBasePlanId = "cycle-3m" });
            plus.Prices.Add(new PlanPrice { Id = PriceId(index, 12), CycleMonths = 12, PriceInr = m12, IsEnabled = true, PlayBasePlanId = "cycle-12m" });

            db.Plans.AddRange(free, plus);
        }
        await db.SaveChangesAsync();
    }

    // Moves seeded plan prices that still hold the first guess to the Play Console price points; an admin edit is left alone.
    private static async Task SyncSeedPlayPricesAsync(ApplicationDbContext db)
    {
        var changes = new (int Feature, int Cycle, int Old, int New)[]
        {
            (1, 1, 199, 200), (1, 3, 537, 550), (1, 12, 1910, 1900),
            (2, 1, 199, 200), (2, 3, 537, 550), (2, 12, 1910, 1900),
            (3, 1, 399, 400), (3, 3, 1077, 1100), (3, 12, 3830, 3800),
            (4, 1, 149, 150), (4, 3, 402, 400), (4, 12, 1430, 1400),
        };
        static Guid PriceId(int feature, int cycle) => new($"b2000000-0000-0000-0000-00000000{feature:D2}{cycle:D2}");

        var ids = changes.Select(c => PriceId(c.Feature, c.Cycle)).ToList();
        var rows = await db.PlanPrices.Where(p => ids.Contains(p.Id)).ToListAsync();
        var changed = false;
        foreach (var c in changes)
        {
            var row = rows.FirstOrDefault(r => r.Id == PriceId(c.Feature, c.Cycle));
            if (row == null || row.PriceInr != c.Old) continue;
            row.PriceInr = c.New;
            changed = true;
        }
        if (changed) await db.SaveChangesAsync();
    }

    private static async Task SeedRoomTypesAsync(ApplicationDbContext db)
    {
        if (await db.RoomTypes.AnyAsync()) return;

        var roomTypes = new[]
        {
            new RoomType { Id = Guid.NewGuid(), Name = "1BHK",   SortOrder = 1, Description = "1 bedroom, hall and kitchen",          CreatedAt = DateTime.UtcNow },
            new RoomType { Id = Guid.NewGuid(), Name = "2BHK",   SortOrder = 2, Description = "2 bedroom, hall and kitchen",          CreatedAt = DateTime.UtcNow },
            new RoomType { Id = Guid.NewGuid(), Name = "3BHK",   SortOrder = 3, Description = "3 bedroom, hall and kitchen",          CreatedAt = DateTime.UtcNow },
            new RoomType { Id = Guid.NewGuid(), Name = "1RK",    SortOrder = 4, Description = "Single room with kitchen",             CreatedAt = DateTime.UtcNow },
            new RoomType { Id = Guid.NewGuid(), Name = "PG",     SortOrder = 5, Description = "Paying guest accommodation",           CreatedAt = DateTime.UtcNow },
            new RoomType { Id = Guid.NewGuid(), Name = "House",  SortOrder = 6, Description = "Independent house",                      CreatedAt = DateTime.UtcNow },
        };

        db.RoomTypes.AddRange(roomTypes);
        await db.SaveChangesAsync();
    }

    private static async Task SeedPlotTypesAsync(ApplicationDbContext db)
    {
        if (await db.PlotTypes.AnyAsync()) return;

        var plotTypes = new[]
        {
            new PlotType { Id = Guid.Parse("b1000000-0000-0000-0000-000000000001"), Name = "Residential",  SortOrder = 1, Description = "Residential land for housing",     CreatedAt = DateTime.UtcNow },
            new PlotType { Id = Guid.Parse("b1000000-0000-0000-0000-000000000002"), Name = "Commercial",   SortOrder = 2, Description = "Commercial land for business use",  CreatedAt = DateTime.UtcNow },
            new PlotType { Id = Guid.Parse("b1000000-0000-0000-0000-000000000003"), Name = "Agricultural", SortOrder = 3, Description = "Agricultural land for farming use", CreatedAt = DateTime.UtcNow },
            new PlotType { Id = Guid.Parse("b1000000-0000-0000-0000-000000000004"), Name = "Farmhouse",    SortOrder = 4, Description = "Farmhouse land for weekend/leisure homes", CreatedAt = DateTime.UtcNow },
        };

        db.PlotTypes.AddRange(plotTypes);
        await db.SaveChangesAsync();
    }

    private static async Task SeedStayTypesAsync(ApplicationDbContext db)
    {
        if (await db.StayTypes.AnyAsync()) return;

        var stayTypes = new[]
        {
            new StayType { Id = Guid.Parse("e1000000-0000-0000-0000-000000000001"), Name = "Homestay",  SortOrder = 1, Description = "Stay in the host's home",         CreatedAt = DateTime.UtcNow },
            new StayType { Id = Guid.Parse("e1000000-0000-0000-0000-000000000002"), Name = "Villa",     SortOrder = 2, Description = "Private villa or bungalow",       CreatedAt = DateTime.UtcNow },
            new StayType { Id = Guid.Parse("e1000000-0000-0000-0000-000000000003"), Name = "Resort",    SortOrder = 3, Description = "Resort with shared facilities",   CreatedAt = DateTime.UtcNow },
            new StayType { Id = Guid.Parse("e1000000-0000-0000-0000-000000000004"), Name = "Cottage",   SortOrder = 4, Description = "Small cottage or cabin",          CreatedAt = DateTime.UtcNow },
            new StayType { Id = Guid.Parse("e1000000-0000-0000-0000-000000000005"), Name = "Farm stay", SortOrder = 5, Description = "Stay on a farm or orchard",       CreatedAt = DateTime.UtcNow },
        };

        db.StayTypes.AddRange(stayTypes);
        await db.SaveChangesAsync();
    }

    // Per-row seeding re-adds any missing seed row, so seeded rows must never be hard-deleted
    // (the admin delete handlers check these and ask to turn the row off or rename it instead).
    private static readonly Regex SeededJobLookupId = new(@"^f[1-6]000000-0000-0000-0000-0000000000[0-9]{2}$");
    private static readonly Regex SeededReportReasonId = new(@"^c1000000-0000-0000-0000-0000000000[0-9]{2}$");

    public static bool IsSeededJobLookup(Guid id) => SeededJobLookupId.IsMatch(id.ToString());
    public static bool IsSeededReportReason(Guid id) => SeededReportReasonId.IsMatch(id.ToString());

    // Per-row incremental like report reasons: a row is added only when neither its Id nor its Name
    // (for roles: the same department and Name) exists yet. Departments are saved before roles.
    private static async Task SeedStayJobLookupsAsync(ApplicationDbContext db)
    {
        static Guid SeedId(char table, int n) => Guid.Parse($"f{table}000000-0000-0000-0000-0000000000{n:D2}");

        await SeedJobLookupAsync(db.JobDepartments, new (int, string)[]
        {
            (1, "Front Office"), (2, "Housekeeping"), (3, "Food & Beverage"), (4, "Kitchen"), (5, "Sales & Marketing"),
            (6, "Management"), (7, "Maintenance"), (8, "Security"), (9, "Guest Services"),
        }, n => SeedId('1', n), db);

        var roles = new (int N, int Dept, string Name, int Sort)[]
        {
            (1, 1, "Front Office Manager", 1), (2, 1, "Front Desk Executive", 2), (3, 1, "Guest Relations Executive", 3),
            (4, 1, "Night Auditor", 4), (5, 1, "Receptionist", 5),
            (6, 2, "Housekeeping Manager", 1), (7, 2, "Room Attendant", 2), (8, 2, "Laundry Supervisor", 3),
            (9, 2, "Public Area Attendant", 4), (10, 2, "Housekeeper", 5), (11, 2, "Caretaker", 6),
            (12, 3, "F&B Manager", 1), (13, 3, "Restaurant Supervisor", 2), (14, 3, "Waiter / Steward", 3), (15, 3, "Bartender", 4),
            (16, 4, "Executive Chef", 1), (17, 4, "Sous Chef", 2), (18, 4, "Commis Chef", 3), (19, 4, "Kitchen Steward", 4),
            (20, 4, "Cook", 5), (21, 4, "Kitchen Helper", 6),
            (22, 5, "Sales Manager", 1), (23, 5, "Reservations Executive", 2), (24, 5, "Marketing Executive", 3),
            (25, 5, "Digital Marketing Executive", 4),
            (26, 6, "General Manager", 1), (27, 6, "Assistant General Manager", 2), (28, 6, "Duty Manager", 3),
            (29, 6, "Guest Experience Manager", 4), (30, 6, "Property Manager", 5),
            (31, 7, "Maintenance Manager", 1), (32, 7, "Electrician", 2), (33, 7, "Plumber", 3), (34, 7, "HVAC Technician", 4),
            (35, 7, "Gardener", 5), (36, 7, "Handyman", 6),
            (37, 8, "Security Supervisor", 1), (38, 8, "Security Guard", 2), (39, 8, "CCTV Operator", 3), (40, 8, "Watchman", 4),
            (41, 9, "Guide", 1), (42, 9, "Driver", 2), (43, 9, "Activity Instructor", 3),
        };

        var deptIds = (await db.JobDepartments.Select(d => d.Id).ToListAsync()).ToHashSet();
        var existingRoles = await db.JobRoles.Select(r => new { r.Id, r.JobDepartmentId, r.Name }).ToListAsync();
        var roleIds = existingRoles.Select(r => r.Id).ToHashSet();
        var roleKeys = existingRoles.Select(r => (r.JobDepartmentId, r.Name.ToLowerInvariant())).ToHashSet();
        var now = DateTime.UtcNow;
        var rolesToAdd = roles
            .Select(r => new JobRole
            {
                Id = SeedId('2', r.N), JobDepartmentId = SeedId('1', r.Dept), Name = r.Name, SortOrder = r.Sort,
                IsActive = true, CreatedAt = now, UpdatedAt = now,
            })
            .Where(r => deptIds.Contains(r.JobDepartmentId)
                && !roleIds.Contains(r.Id) && !roleKeys.Contains((r.JobDepartmentId, r.Name.ToLowerInvariant())))
            .ToList();
        if (rolesToAdd.Count > 0)
        {
            db.JobRoles.AddRange(rolesToAdd);
            await db.SaveChangesAsync();
        }

        await SeedJobLookupAsync(db.JobTypes, new (int, string)[]
        {
            (1, "Full-Time"), (2, "Part-Time"), (3, "Contract"), (4, "Internship"),
        }, n => SeedId('4', n), db);
        await SeedJobLookupAsync(db.JobShiftTypes, new (int, string)[]
        {
            (1, "Day Shift"), (2, "Night Shift"), (3, "Rotational"), (4, "Split Shift"),
        }, n => SeedId('5', n), db);
        await SeedJobLookupAsync(db.JobSalaryTypes, new (int, string)[]
        {
            (1, "Monthly"), (2, "Fixed"), (3, "Negotiable"),
        }, n => SeedId('6', n), db);
    }

    // Sort order equals the row number for these flat lists.
    private static async Task SeedJobLookupAsync<T>(
        DbSet<T> set, (int N, string Name)[] rows, Func<int, Guid> id, ApplicationDbContext db)
        where T : JobLookupBase, new()
    {
        var existing = await set.Select(x => new { x.Id, x.Name }).ToListAsync();
        var existingIds = existing.Select(x => x.Id).ToHashSet();
        var existingNames = existing.Select(x => x.Name.ToLowerInvariant()).ToHashSet();
        var now = DateTime.UtcNow;

        var toAdd = rows
            .Select(r => new T { Id = id(r.N), Name = r.Name, SortOrder = r.N, IsActive = true, CreatedAt = now, UpdatedAt = now })
            .Where(x => !existingIds.Contains(x.Id) && !existingNames.Contains(x.Name.ToLowerInvariant()))
            .ToList();
        if (toAdd.Count == 0) return;

        set.AddRange(toAdd);
        await db.SaveChangesAsync();
    }

    // Per-row incremental: a row is added only when neither its Id, Name nor Code exists yet.
    private static async Task SeedStayRegistrationTypesAsync(ApplicationDbContext db)
    {
        static Guid SeedId(int n) => Guid.Parse($"e2000000-0000-0000-0000-0000000000{n:D2}");

        var rows = new (int N, string Code, string Name, string HelpText, bool RequiresOtherName)[]
        {
            (1, "UK_HOMESTAY", "Uttarakhand Homestay registration", "Number on your Homestay registration certificate", false),
            (2, "TOURISM", "Tourism / hotel registration", "Number on your tourism or hotel registration", false),
            (3, "TRADE_LICENCE", "Trade licence / shop establishment", "Licence number as printed", false),
            (4, StayRegistrationRules.UdyamCode, "Udyam (MSME)", "e.g. UDYAM-UK-05-0001234", false),
            (5, StayRegistrationRules.OtherCode, "Other", "Name the document and enter its number", true),
        };

        var existing = await db.StayRegistrationTypes.Select(t => new { t.Id, t.Name, t.Code }).ToListAsync();
        var existingIds = existing.Select(t => t.Id).ToHashSet();
        var existingNames = existing.Select(t => t.Name.ToLowerInvariant()).ToHashSet();
        var existingCodes = existing.Select(t => t.Code).ToHashSet();
        var now = DateTime.UtcNow;

        var toAdd = rows
            .Select(r => new StayRegistrationType
            {
                Id = SeedId(r.N), Code = r.Code, Name = r.Name, HelpText = r.HelpText, SortOrder = r.N,
                RequiresOtherName = r.RequiresOtherName, IsActive = true, CreatedAt = now,
            })
            .Where(t => !existingIds.Contains(t.Id) && !existingNames.Contains(t.Name.ToLowerInvariant())
                && !existingCodes.Contains(t.Code))
            .ToList();
        if (toAdd.Count == 0) return;

        db.StayRegistrationTypes.AddRange(toAdd);
        await db.SaveChangesAsync();
    }

    // Per-row incremental like the job lookups: a row is added only when neither its Id nor its Name
    // exists yet. Seeded rows can only be switched off (TaxiVehicleTypeSeed.IsSeeded guards delete).
    private static async Task SeedTaxiVehicleTypesAsync(ApplicationDbContext db)
    {
        static Guid TaxiSeedId(int n) => Guid.Parse($"a1000000-0000-0000-0000-0000000000{n:D2}");

        const string H = TaxiVehicleCategories.Hatchback, S = TaxiVehicleCategories.Sedan, M = TaxiVehicleCategories.Muv,
            U = TaxiVehicleCategories.Suv, T = TaxiVehicleCategories.TempoTraveller, B = TaxiVehicleCategories.Bus;

        var rows = new (int N, string Name, string Category, short Seats, int Sort)[]
        {
            (1, "Maruti Alto", H, 4, 1), (2, "Maruti WagonR", H, 4, 2), (3, "Maruti Swift", H, 4, 3),
            (4, "Maruti Celerio", H, 4, 4), (5, "Hyundai Grand i10", H, 4, 5), (6, "Hyundai i20", H, 4, 6),
            (7, "Tata Tiago", H, 4, 7), (8, "Renault Kwid", H, 4, 8),
            (9, "Maruti Swift Dzire", S, 4, 10), (10, "Toyota Etios", S, 4, 11), (11, "Hyundai Aura", S, 4, 12),
            (12, "Hyundai Xcent", S, 4, 13), (13, "Honda Amaze", S, 4, 14), (14, "Honda City", S, 4, 15),
            (15, "Tata Tigor", S, 4, 16), (16, "Maruti Ciaz", S, 4, 17), (17, "Hyundai Verna", S, 4, 18),
            (18, "Maruti Ertiga", M, 6, 20), (19, "Toyota Innova", M, 7, 21), (20, "Toyota Innova Crysta", M, 7, 22),
            (21, "Toyota Innova Hycross", M, 7, 23), (22, "Mahindra Marazzo", M, 7, 24), (23, "Chevrolet Tavera", M, 7, 25),
            (24, "Mahindra Xylo", M, 7, 26), (25, "Kia Carens", M, 6, 27), (26, "Renault Triber", M, 6, 28),
            (27, "Mahindra Bolero", U, 7, 30), (28, "Mahindra Scorpio", U, 7, 31), (29, "Mahindra Scorpio-N", U, 7, 32),
            (30, "Mahindra XUV500", U, 7, 33), (31, "Mahindra XUV700", U, 7, 34), (32, "Tata Sumo", U, 7, 35),
            (33, "Tata Sumo Gold", U, 7, 36), (34, "Toyota Fortuner", U, 7, 37), (35, "Maruti Brezza", U, 4, 38),
            (36, "Hyundai Creta", U, 4, 39), (37, "Mahindra Thar", U, 4, 40), (38, "Force Gurkha", U, 4, 41),
            (39, "Tata Safari", U, 7, 42),
            (40, "Tempo Traveller 9 seater", T, 7, 50), (41, "Tempo Traveller 12 seater", T, 12, 51),
            (42, "Tempo Traveller 17 seater", T, 12, 52), (43, "Tempo Traveller 20 seater", T, 12, 53),
            (44, "Force Urbania", T, 12, 54),
            (45, "Mini Bus 26 seater", B, 12, 60), (46, "Mini Bus 32 seater", B, 12, 61),
        };

        var existing = await db.TaxiVehicleTypes.Select(t => new { t.Id, t.Name }).ToListAsync();
        var existingIds = existing.Select(t => t.Id).ToHashSet();
        var existingNames = existing.Select(t => t.Name.ToLowerInvariant()).ToHashSet();
        var now = DateTime.UtcNow;

        var toAdd = rows
            .Select(r => new TaxiVehicleType
            {
                Id = TaxiSeedId(r.N), Name = r.Name, Category = r.Category, DefaultSeats = r.Seats,
                SortOrder = r.Sort, IsActive = true, CreatedAt = now,
            })
            .Where(t => !existingIds.Contains(t.Id) && !existingNames.Contains(t.Name.ToLowerInvariant()))
            .ToList();
        if (toAdd.Count == 0) return;

        db.TaxiVehicleTypes.AddRange(toAdd);
        await db.SaveChangesAsync();
    }

    // Per-row incremental: a row is added only when neither its Id nor its Name exists yet, so new
    // reasons reach databases that already hold some, and admin-made reasons are never duplicated.
    private static async Task SeedReportReasonsAsync(ApplicationDbContext db)
    {
        var existing = await db.ReportReasons.Select(r => new { r.Id, r.Name }).ToListAsync();
        var existingIds = existing.Select(r => r.Id).ToHashSet();
        var existingNames = existing.Select(r => r.Name.ToLowerInvariant()).ToHashSet();

        var reasons = new[]
        {
            new ReportReason { Id = Guid.Parse("c1000000-0000-0000-0000-000000000001"), Name = "Incorrect information", SortOrder = 1, Description = "Price, location or photos don't match the actual property", CreatedAt = DateTime.UtcNow },
            new ReportReason { Id = Guid.Parse("c1000000-0000-0000-0000-000000000002"), Name = "Offensive content",     SortOrder = 2, Description = "Contains nudity, abusive language or hate speech",          CreatedAt = DateTime.UtcNow },
            new ReportReason { Id = Guid.Parse("c1000000-0000-0000-0000-000000000003"), Name = "Fake or not available", SortOrder = 3, Description = "The place doesn't exist or isn't open to guests or tenants", CreatedAt = DateTime.UtcNow },
            new ReportReason { Id = Guid.Parse("c1000000-0000-0000-0000-000000000004"), Name = "Asked for advance payment", SortOrder = 4, Description = "Owner asked for money before a visit or stay",           CreatedAt = DateTime.UtcNow },
            new ReportReason { Id = Guid.Parse("c1000000-0000-0000-0000-000000000005"), Name = "Wrong contact details", SortOrder = 5, Description = "Phone number doesn't work or isn't the owner's",          CreatedAt = DateTime.UtcNow },
        };

        var toAdd = reasons.Where(r => !existingIds.Contains(r.Id) && !existingNames.Contains(r.Name.ToLowerInvariant())).ToList();
        if (toAdd.Count == 0) return;

        db.ReportReasons.AddRange(toAdd);
        await db.SaveChangesAsync();
    }

    // Per-key incremental seeding, not a whole-table AnyAsync() guard (unlike the other Seed*
    // methods in this file) — this catalog is expected to grow after initial deploy (this method
    // itself already went from 4 to 13 rows once), and a whole-table guard would silently skip
    // every row added here on any environment that already has at least one row, including the
    // fresh-database case if a future migration ever pre-populates a subset. Existing rows'
    // Ids/Keys are untouched, so this doesn't disturb the QuestionTemplateId-independent catalog
    // lookups (messages store `key`, not Id — see the schema note in CHAT_FEATURE.md §2).
    private static async Task SeedQuestionTemplatesAsync(ApplicationDbContext db)
    {
        var existingKeys = (await db.QuestionTemplates.Select(t => t.Key).ToListAsync()).ToHashSet();

        var pgRoomTypeId = await db.RoomTypes.Where(r => r.Name == "PG").Select(r => (Guid?)r.Id).FirstOrDefaultAsync();

        var templates = new List<QuestionTemplate>
        {
            new()
            {
                Id = Guid.Parse("d1000000-0000-0000-0000-000000000001"),
                Key = "is_available",
                ListingType = "Both",
                QuestionText = "Is it still available?",
                AnswerOptionsJson = "[{\"key\":\"yes_available\",\"text\":\"Yes, still available\",\"sentiment\":\"positive\"},{\"key\":\"no_available\",\"text\":\"No, already taken\",\"sentiment\":\"negative\"}]",
                SortOrder = 1,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            },
            new()
            {
                Id = Guid.Parse("d1000000-0000-0000-0000-000000000002"),
                Key = "is_rent_negotiable",
                ListingType = "Room",
                QuestionText = "Is rent negotiable?",
                AnswerOptionsJson = "[{\"key\":\"yes_negotiable\",\"text\":\"Yes, a little\",\"sentiment\":\"positive\"},{\"key\":\"no_negotiable\",\"text\":\"No, price is fixed\",\"sentiment\":\"negative\"}]",
                SortOrder = 2,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            },
            new()
            {
                Id = Guid.Parse("d1000000-0000-0000-0000-000000000003"),
                Key = "is_price_negotiable",
                ListingType = "Plot",
                QuestionText = "Is price negotiable?",
                AnswerOptionsJson = "[{\"key\":\"yes_negotiable\",\"text\":\"Yes, a little\",\"sentiment\":\"positive\"},{\"key\":\"no_negotiable\",\"text\":\"No, price is fixed\",\"sentiment\":\"negative\"}]",
                SortOrder = 2,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            },
            new()
            {
                Id = Guid.Parse("d1000000-0000-0000-0000-000000000004"),
                Key = "is_fenced",
                ListingType = "Plot",
                QuestionText = "Is it fenced / boundary marked?",
                AnswerOptionsJson = "[{\"key\":\"yes_fenced\",\"text\":\"Yes, fully fenced\",\"sentiment\":\"positive\"},{\"key\":\"no_fenced\",\"text\":\"No, not yet\",\"sentiment\":\"negative\"}]",
                SortOrder = 3,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            },

            // ── Added after launch (2026-07-12) — none of these duplicate a field already
            // shown on View Details, verified against RoomListing/PlotListing (no deposit,
            // brokerage, parking, maintenance, electricity, water or road-access column on
            // either entity). Kept broad (no RoomTypeId/PlotTypeId) except food_included,
            // which is the one case a single subtype is unambiguous — narrowing the rest to
            // "some but not all" subtypes would need a separate template row per subtype
            // (RoomTypeId/PlotTypeId is a single nullable FK, not a set), which isn't worth
            // the duplication for a first pass.
            new()
            {
                Id = Guid.Parse("d1000000-0000-0000-0000-000000000005"),
                Key = "has_brokerage",
                ListingType = "Both",
                QuestionText = "Is there any brokerage/agent fee?",
                AnswerOptionsJson = "[{\"key\":\"no_brokerage\",\"text\":\"No, direct from owner\",\"sentiment\":\"positive\"},{\"key\":\"yes_brokerage\",\"text\":\"Yes, brokerage applies\",\"sentiment\":\"negative\"}]",
                SortOrder = 4,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            },
            new()
            {
                Id = Guid.Parse("d1000000-0000-0000-0000-000000000006"),
                Key = "requires_deposit",
                ListingType = "Room",
                QuestionText = "Is security deposit required?",
                AnswerOptionsJson = "[{\"key\":\"yes_deposit\",\"text\":\"Yes, deposit required\",\"sentiment\":\"negative\"},{\"key\":\"no_deposit\",\"text\":\"No deposit needed\",\"sentiment\":\"positive\"}]",
                SortOrder = 5,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            },
            new()
            {
                Id = Guid.Parse("d1000000-0000-0000-0000-000000000007"),
                Key = "room_maintenance_included",
                ListingType = "Room",
                QuestionText = "Is maintenance fee included in rent?",
                AnswerOptionsJson = "[{\"key\":\"yes_maintenance_included\",\"text\":\"Yes, included in rent\",\"sentiment\":\"positive\"},{\"key\":\"no_maintenance_extra\",\"text\":\"No, charged separately\",\"sentiment\":\"negative\"}]",
                SortOrder = 6,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            },
            new()
            {
                Id = Guid.Parse("d1000000-0000-0000-0000-000000000008"),
                Key = "food_included",
                ListingType = "Room",
                RoomTypeId = pgRoomTypeId,
                QuestionText = "Is food included?",
                AnswerOptionsJson = "[{\"key\":\"yes_food\",\"text\":\"Yes, food included\",\"sentiment\":\"positive\"},{\"key\":\"no_food\",\"text\":\"No, self-arranged\",\"sentiment\":\"negative\"}]",
                SortOrder = 7,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            },
            new()
            {
                Id = Guid.Parse("d1000000-0000-0000-0000-000000000009"),
                Key = "has_parking",
                ListingType = "Plot",
                QuestionText = "Is parking space available?",
                AnswerOptionsJson = "[{\"key\":\"yes_parking\",\"text\":\"Yes, parking available\",\"sentiment\":\"positive\"},{\"key\":\"no_parking\",\"text\":\"No parking\",\"sentiment\":\"negative\"}]",
                SortOrder = 4,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            },
            new()
            {
                Id = Guid.Parse("d1000000-0000-0000-0000-000000000010"),
                Key = "plot_maintenance_charge",
                ListingType = "Plot",
                QuestionText = "Is there a maintenance/society charge?",
                AnswerOptionsJson = "[{\"key\":\"yes_plot_maintenance\",\"text\":\"Yes, charge applies\",\"sentiment\":\"negative\"},{\"key\":\"no_plot_maintenance\",\"text\":\"No maintenance charge\",\"sentiment\":\"positive\"}]",
                SortOrder = 5,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            },
            new()
            {
                Id = Guid.Parse("d1000000-0000-0000-0000-000000000011"),
                Key = "has_electricity",
                ListingType = "Plot",
                QuestionText = "Is electricity connection available?",
                AnswerOptionsJson = "[{\"key\":\"yes_electricity\",\"text\":\"Yes, connection available\",\"sentiment\":\"positive\"},{\"key\":\"no_electricity\",\"text\":\"No, needs to be arranged\",\"sentiment\":\"negative\"}]",
                SortOrder = 6,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            },
            new()
            {
                Id = Guid.Parse("d1000000-0000-0000-0000-000000000012"),
                Key = "has_water",
                ListingType = "Plot",
                QuestionText = "Is water source available (borewell/municipal)?",
                AnswerOptionsJson = "[{\"key\":\"yes_water\",\"text\":\"Yes, water source available\",\"sentiment\":\"positive\"},{\"key\":\"no_water\",\"text\":\"No, needs to be arranged\",\"sentiment\":\"negative\"}]",
                SortOrder = 7,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            },
            new()
            {
                Id = Guid.Parse("d1000000-0000-0000-0000-000000000013"),
                Key = "has_road_access",
                ListingType = "Plot",
                QuestionText = "Is there proper approach road access?",
                AnswerOptionsJson = "[{\"key\":\"yes_road_access\",\"text\":\"Yes, road access available\",\"sentiment\":\"positive\"},{\"key\":\"no_road_access\",\"text\":\"No proper road yet\",\"sentiment\":\"negative\"}]",
                SortOrder = 8,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            },

            // Stay rows: only things the stay form does not already show (dates, extra guests,
            // food type, pickup, visitors, discount, early check-in). "Both" stays Room and Plot.
            new()
            {
                Id = Guid.Parse("d1000000-0000-0000-0000-000000000014"),
                Key = "stay_is_available",
                ListingType = ListingKinds.Stay,
                QuestionText = "Is it available this weekend?",
                AnswerOptionsJson = "[{\"key\":\"yes_stay_available\",\"text\":\"Yes, available\",\"sentiment\":\"positive\"},{\"key\":\"no_stay_available\",\"text\":\"No, fully booked\",\"sentiment\":\"negative\"}]",
                SortOrder = 1,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            },
            new()
            {
                Id = Guid.Parse("d1000000-0000-0000-0000-000000000015"),
                Key = "stay_dates_free",
                ListingType = ListingKinds.Stay,
                QuestionText = "Is it free on my dates?",
                AnswerOptionsJson = "[{\"key\":\"yes_dates_free\",\"text\":\"Yes, dates are free\",\"sentiment\":\"positive\"},{\"key\":\"call_to_confirm\",\"text\":\"Call me to confirm\",\"sentiment\":\"neutral\"},{\"key\":\"no_dates_free\",\"text\":\"No, already booked\",\"sentiment\":\"negative\"}]",
                SortOrder = 2,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            },
            new()
            {
                Id = Guid.Parse("d1000000-0000-0000-0000-000000000016"),
                Key = "stay_extra_guest",
                ListingType = ListingKinds.Stay,
                QuestionText = "Can one extra guest stay?",
                AnswerOptionsJson = "[{\"key\":\"yes_extra_free\",\"text\":\"Yes, no extra cost\",\"sentiment\":\"positive\"},{\"key\":\"yes_extra_paid\",\"text\":\"Yes, extra charge applies\",\"sentiment\":\"neutral\"},{\"key\":\"no_extra_guest\",\"text\":\"No, listed guests only\",\"sentiment\":\"negative\"}]",
                SortOrder = 3,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            },
            new()
            {
                Id = Guid.Parse("d1000000-0000-0000-0000-000000000017"),
                Key = "stay_food_type",
                ListingType = ListingKinds.Stay,
                QuestionText = "What food is served?",
                AnswerOptionsJson = "[{\"key\":\"veg_nonveg\",\"text\":\"Veg and non-veg\",\"sentiment\":\"positive\"},{\"key\":\"veg_only\",\"text\":\"Veg only\",\"sentiment\":\"neutral\"},{\"key\":\"no_food_served\",\"text\":\"No food served\",\"sentiment\":\"negative\"}]",
                SortOrder = 4,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            },
            new()
            {
                Id = Guid.Parse("d1000000-0000-0000-0000-000000000018"),
                Key = "stay_pickup",
                ListingType = ListingKinds.Stay,
                QuestionText = "Is pickup from the bus stand available?",
                AnswerOptionsJson = "[{\"key\":\"yes_pickup_free\",\"text\":\"Yes, free pickup\",\"sentiment\":\"positive\"},{\"key\":\"yes_pickup_paid\",\"text\":\"Yes, extra charge\",\"sentiment\":\"neutral\"},{\"key\":\"no_pickup\",\"text\":\"No pickup\",\"sentiment\":\"negative\"}]",
                SortOrder = 5,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            },
            new()
            {
                Id = Guid.Parse("d1000000-0000-0000-0000-000000000019"),
                Key = "stay_visitors",
                ListingType = ListingKinds.Stay,
                QuestionText = "Are outside visitors allowed?",
                AnswerOptionsJson = "[{\"key\":\"yes_visitors\",\"text\":\"Yes, visitors allowed\",\"sentiment\":\"positive\"},{\"key\":\"no_visitors\",\"text\":\"No visitors\",\"sentiment\":\"negative\"}]",
                SortOrder = 6,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            },
            new()
            {
                Id = Guid.Parse("d1000000-0000-0000-0000-000000000020"),
                Key = "stay_long_stay_discount",
                ListingType = ListingKinds.Stay,
                QuestionText = "Any discount for a longer stay?",
                AnswerOptionsJson = "[{\"key\":\"yes_discount\",\"text\":\"Yes, a discount is possible\",\"sentiment\":\"positive\"},{\"key\":\"no_discount\",\"text\":\"No, price is fixed\",\"sentiment\":\"negative\"}]",
                SortOrder = 7,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            },
            new()
            {
                Id = Guid.Parse("d1000000-0000-0000-0000-000000000021"),
                Key = "stay_early_checkin",
                ListingType = ListingKinds.Stay,
                QuestionText = "Is early check-in possible?",
                AnswerOptionsJson = "[{\"key\":\"yes_early_checkin\",\"text\":\"Yes, if the room is free\",\"sentiment\":\"positive\"},{\"key\":\"no_early_checkin\",\"text\":\"No, standard time only\",\"sentiment\":\"negative\"}]",
                SortOrder = 8,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            },
            new()
            {
                Id = Guid.Parse("d1000000-0000-0000-0000-000000000022"),
                Key = "stay_price_negotiable",
                ListingType = ListingKinds.Stay,
                QuestionText = "Is the price negotiable?",
                AnswerOptionsJson = "[{\"key\":\"yes_stay_negotiable\",\"text\":\"Yes, a little\",\"sentiment\":\"positive\"},{\"key\":\"no_stay_negotiable\",\"text\":\"Price is fixed\",\"sentiment\":\"negative\"},{\"key\":\"call_to_talk_price\",\"text\":\"Call me to talk\",\"sentiment\":\"neutral\"}]",
                SortOrder = 9,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            },
            new()
            {
                Id = Guid.Parse("d1000000-0000-0000-0000-000000000023"),
                Key = "stay_final_total",
                ListingType = ListingKinds.Stay,
                QuestionText = "What is the final total?",
                AnswerOptionsJson = "[{\"key\":\"total_in_chat\",\"text\":\"I will send the total here\",\"sentiment\":\"positive\"},{\"key\":\"total_on_call\",\"text\":\"Call me for the total\",\"sentiment\":\"neutral\"}]",
                SortOrder = 10,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            },
            new()
            {
                Id = Guid.Parse("d1000000-0000-0000-0000-000000000024"),
                Key = "stay_advance_needed",
                ListingType = ListingKinds.Stay,
                QuestionText = "Is an advance needed?",
                AnswerOptionsJson = "[{\"key\":\"advance_needed\",\"text\":\"Yes, a small advance\",\"sentiment\":\"neutral\"},{\"key\":\"no_advance_needed\",\"text\":\"No advance, pay at check-in\",\"sentiment\":\"positive\"},{\"key\":\"call_for_advance\",\"text\":\"Call me to talk\",\"sentiment\":\"neutral\"}]",
                SortOrder = 11,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            },
            new()
            {
                Id = Guid.Parse("d1000000-0000-0000-0000-000000000025"),
                Key = "stay_cancellation",
                ListingType = ListingKinds.Stay,
                QuestionText = "What is the cancellation and refund rule?",
                AnswerOptionsJson = "[{\"key\":\"cancel_free\",\"text\":\"Free cancellation before check-in\",\"sentiment\":\"positive\"},{\"key\":\"cancel_partial\",\"text\":\"Partial refund\",\"sentiment\":\"neutral\"},{\"key\":\"cancel_none\",\"text\":\"No refund\",\"sentiment\":\"negative\"},{\"key\":\"cancel_call\",\"text\":\"Call me to talk\",\"sentiment\":\"neutral\"}]",
                SortOrder = 12,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            },
            new()
            {
                Id = Guid.Parse("d1000000-0000-0000-0000-000000000026"),
                Key = "stay_group_rate",
                ListingType = ListingKinds.Stay,
                QuestionText = "Is there a group or extra room rate?",
                AnswerOptionsJson = "[{\"key\":\"group_rate_yes\",\"text\":\"Yes, a group rate is possible\",\"sentiment\":\"positive\"},{\"key\":\"group_rate_no\",\"text\":\"No, standard rate\",\"sentiment\":\"negative\"},{\"key\":\"group_rate_call\",\"text\":\"Call me to talk\",\"sentiment\":\"neutral\"}]",
                SortOrder = 13,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            },
            new()
            {
                Id = Guid.Parse("d1000000-0000-0000-0000-000000000027"),
                Key = "stay_gst_invoice",
                ListingType = ListingKinds.Stay,
                QuestionText = "Can I get a GST invoice?",
                AnswerOptionsJson = "[{\"key\":\"gst_yes\",\"text\":\"Yes, GST invoice available\",\"sentiment\":\"positive\"},{\"key\":\"gst_no\",\"text\":\"No GST invoice\",\"sentiment\":\"negative\"}]",
                SortOrder = 14,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            },
            new()
            {
                Id = Guid.Parse("d1000000-0000-0000-0000-000000000028"),
                Key = "stay_pay_on_arrival",
                ListingType = ListingKinds.Stay,
                QuestionText = "Can I pay on arrival?",
                AnswerOptionsJson = "[{\"key\":\"pay_arrival_yes\",\"text\":\"Yes, pay at check-in\",\"sentiment\":\"positive\"},{\"key\":\"pay_arrival_advance\",\"text\":\"Advance first, rest on arrival\",\"sentiment\":\"neutral\"},{\"key\":\"pay_arrival_no\",\"text\":\"No, full payment before\",\"sentiment\":\"negative\"}]",
                SortOrder = 15,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            },
            new()
            {
                Id = Guid.Parse("d1000000-0000-0000-0000-000000000029"),
                Key = "stay_ground_floor",
                ListingType = ListingKinds.Stay,
                QuestionText = "Is there a ground floor room or a lift?",
                AnswerOptionsJson = "[{\"key\":\"ground_floor_yes\",\"text\":\"Yes, ground floor room\",\"sentiment\":\"positive\"},{\"key\":\"lift_yes\",\"text\":\"Yes, there is a lift\",\"sentiment\":\"positive\"},{\"key\":\"no_ground_floor\",\"text\":\"No ground floor or lift\",\"sentiment\":\"negative\"}]",
                SortOrder = 16,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            },
            new()
            {
                Id = Guid.Parse("d1000000-0000-0000-0000-000000000030"),
                Key = "stay_women_safe",
                ListingType = ListingKinds.Stay,
                QuestionText = "Is it women-only or safe for solo travellers?",
                AnswerOptionsJson = "[{\"key\":\"women_only\",\"text\":\"Women-only stay\",\"sentiment\":\"positive\"},{\"key\":\"solo_safe\",\"text\":\"Yes, safe for solo travellers\",\"sentiment\":\"positive\"},{\"key\":\"not_solo_focused\",\"text\":\"Family stay, not solo-focused\",\"sentiment\":\"neutral\"}]",
                SortOrder = 17,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            },
        };

        var toAdd = templates.Where(t => !existingKeys.Contains(t.Key)).ToList();
        if (toAdd.Count == 0) return;

        db.QuestionTemplates.AddRange(toAdd);
        await db.SaveChangesAsync();
    }

    internal static bool SeedOnboardingFlag(string stateName) => OnboardingLaunchStates.Contains(stateName.Trim());

    private static async Task SeedDistrictsAsync(ApplicationDbContext db)
    {
        if (await db.Districts.AnyAsync()) return;

        var asm = typeof(DataSeeder).Assembly;
        await using var stream = asm.GetManifestResourceStream("RentNearBy.Infrastructure.Data.districts.json");
        if (stream == null)
        {
            Console.WriteLine("[DataSeeder] districts.json resource not found — skipping district seeding.");
            return;
        }

        // Deserialize directly from stream — avoids allocating the full JSON as an intermediate string.
        var records = await JsonSerializer.DeserializeAsync<List<DistrictSeedRecord>>(stream, CaseInsensitiveJson);
        if (records == null || records.Count == 0)
        {
            Console.WriteLine("[DataSeeder] districts.json is empty — skipping district seeding.");
            return;
        }

        Console.WriteLine($"[DataSeeder] Seeding {records.Count} districts...");

        var batch = new List<District>(50);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seeded = 0;
        var skippedDuplicate = 0;
        var skippedBadGeometry = 0;

        db.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            for (var position = 0; position < records.Count; position++)
            {
                var record = records[position];
                if (string.IsNullOrWhiteSpace(record.Name)) continue;

                if (!seen.Add($"{record.StateName}|{record.Name}"))
                {
                    skippedDuplicate++;
                    continue;
                }

                Geometry? boundary = null;
                if (!string.IsNullOrWhiteSpace(record.Boundary))
                {
                    try
                    {
                        boundary = WktReader.Read(record.Boundary);
                        boundary.SRID = 4326;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[DataSeeder] Bad geometry for '{record.Name}' ({record.StateName}): {ex.Message}");
                        skippedBadGeometry++;
                    }
                }

                batch.Add(new District
                {
                    Id = StableIds.District(position),
                    Name = record.Name.Trim(),
                    StateName = record.StateName.Trim(),
                    IsActive = true,
                    BusinessOnboardingEnabled = SeedOnboardingFlag(record.StateName),
                    Boundary = boundary,
                    CreatedAt = DateTime.UtcNow,
                });

                if (batch.Count >= 50)
                {
                    db.Districts.AddRange(batch);
                    await db.SaveChangesAsync();
                    db.ChangeTracker.Clear(); // release tracked entities to keep memory flat
                    seeded += batch.Count;
                    batch.Clear();
                }
            }

            if (batch.Count > 0)
            {
                db.Districts.AddRange(batch);
                await db.SaveChangesAsync();
                db.ChangeTracker.Clear();
                seeded += batch.Count;
            }
        }
        finally
        {
            db.ChangeTracker.AutoDetectChangesEnabled = true;
        }

        Console.WriteLine($"[DataSeeder] District seeding complete. Seeded: {seeded}, Duplicates skipped: {skippedDuplicate}, Bad geometry: {skippedBadGeometry}");
    }

    private static async Task SeedCitiesAsync(ApplicationDbContext db, ILogger logger)
    {
        if (await db.Cities.AnyAsync())
        {
            await BackfillCityKeysAsync(db, logger);
            return;
        }

        // Id + Name for every district; the boundary only for names shared by districts of different states.
        var allDistricts = await db.Districts
            .AsNoTracking()
            .Select(d => new { d.Id, d.Name })
            .ToListAsync();

        if (allDistricts.Count == 0)
        {
            Console.WriteLine("[DataSeeder] No districts found — skipping city seeding.");
            return;
        }

        var ambiguousKeys = DistrictNameResolver.AmbiguousKeys(allDistricts.Select(d => (d.Id, d.Name)));
        var ambiguousIds = allDistricts.Where(d => ambiguousKeys.Contains(DistrictNameResolver.Key(d.Name))).Select(d => d.Id).ToList();
        var boundaries = ambiguousIds.Count == 0
            ? new Dictionary<Guid, Geometry?>()
            : await db.Districts.AsNoTracking()
                .Where(d => ambiguousIds.Contains(d.Id))
                .ToDictionaryAsync(d => d.Id, d => d.Boundary);
        var resolver = new DistrictNameResolver(allDistricts.Select(d =>
            new DistrictNameResolver.Candidate(d.Id, d.Name, boundaries.GetValueOrDefault(d.Id))));

        if (ambiguousKeys.Count > 0)
            Console.WriteLine($"[DataSeeder] {ambiguousKeys.Count} district name(s) are shared across states — their cities are placed by point-in-polygon.");

        var records = await LoadFileRowsAsync();
        if (records.Count == 0)
        {
            Console.WriteLine("[DataSeeder] No city rows available — skipping city seeding.");
            return;
        }

        Console.WriteLine($"[DataSeeder] Seeding {records.Count} cities (source=file)...");

        var batch = new List<City>(500);
        var dedupe = new CitySeedDedupe();
        var seeded = 0;
        var skippedNoDistrict = 0;
        var skippedDuplicate = 0;
        var batchCount = 0;

        db.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            foreach (var record in records)
            {
                if (string.IsNullOrWhiteSpace(record.Name) || string.IsNullOrWhiteSpace(record.DistrictName))
                    continue;

                if (resolver.Resolve(record.DistrictName, record.Latitude, record.Longitude) is not Guid districtId)
                {
                    skippedNoDistrict++;
                    continue;
                }

                if (!dedupe.TryAdd(districtId, record.Name, record.Latitude, record.Longitude))
                {
                    skippedDuplicate++;
                    continue;
                }

                batch.Add(new City
                {
                    Id = record.CityKey > 0 ? StableIds.City(record.CityKey) : Guid.NewGuid(),
                    DistrictId = districtId,
                    Name = record.Name.Trim(),
                    Latitude = (decimal)record.Latitude,
                    Longitude = (decimal)record.Longitude,
                    CityKey = record.CityKey,
                    CreatedAt = DateTime.UtcNow,
                });

                if (batch.Count >= 500)
                {
                    db.Cities.AddRange(batch);
                    await db.SaveChangesAsync();
                    db.ChangeTracker.Clear(); // release tracked entities to keep memory flat
                    seeded += batch.Count;
                    batch.Clear();
                    batchCount++;

                    if (batchCount % 20 == 0)
                        Console.WriteLine($"[DataSeeder] Cities progress: {seeded} seeded so far...");
                }
            }

            if (batch.Count > 0)
            {
                db.Cities.AddRange(batch);
                await db.SaveChangesAsync();
                db.ChangeTracker.Clear();
                seeded += batch.Count;
            }
        }
        finally
        {
            db.ChangeTracker.AutoDetectChangesEnabled = true;
        }

        Console.WriteLine($"[DataSeeder] City seeding complete (source=file). Seeded: {seeded}, Skipped (no district): {skippedNoDistrict}, Skipped (same place): {skippedDuplicate}");
    }

    // Seed identity: a row is the same place as an earlier row of its district when the compact names match
    // and the points are within CityNameMatcher.SamePlaceKm; same-name villages farther apart are all kept.
    internal sealed class CitySeedDedupe
    {
        private readonly Dictionary<(Guid DistrictId, string Key), List<(double Lat, double Lng)>> _seen = new();

        public bool TryAdd(Guid districtId, string name, double lat, double lng)
        {
            var key = (districtId, CityNameMatcher.IdentityKey(name));
            if (!_seen.TryGetValue(key, out var points))
            {
                _seen[key] = [(lat, lng)];
                return true;
            }
            if (points.Any(p => CityNameMatcher.SamePlace(p.Lat, p.Lng, lat, lng))) return false;
            points.Add((lat, lng));
            return true;
        }
    }

    // Bigha is a state unit (BighaArea); recomputes AreaSqft of bigha plots whose stored value used another factor.
    private static async Task RecomputeBighaAreasAsync(ApplicationDbContext db, ILogger logger)
    {
        var updated = 0;
        foreach (var (state, factor) in BighaArea.StateFactors)
        {
            updated += await db.PlotListings
                .Where(p => p.AreaUnit == "bigha" && p.District.StateName == state && p.AreaSqft != p.AreaValue * factor)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.AreaSqft, p => p.AreaValue * factor));
        }
        if (updated > 0)
            logger.LogInformation("seed.plots.bigha_area {Updated}", updated);
    }

    // One-time CityKey fill for a database seeded before the key existed; skips once any city has a key.
    private static async Task BackfillCityKeysAsync(ApplicationDbContext db, ILogger logger)
    {
        if (await db.Cities.AnyAsync(c => c.CityKey != null)) return;

        var cities = (await db.Cities
                .AsNoTracking()
                .Select(c => new { c.Id, c.Name, DistrictName = c.District.Name })
                .ToListAsync())
            .Select(c => (c.Id, c.DistrictName, c.Name))
            .ToList();

        // Same-name districts in different states cannot be told apart by name, like the insert path.
        var ambiguousDistricts = (await db.Districts.AsNoTracking().Select(d => d.Name).ToListAsync())
            .GroupBy(n => n.Trim().ToLowerInvariant())
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet();

        var rows = await LoadFileRowsAsync();
        var keys = MatchCityKeys(
            cities.Where(c => !ambiguousDistricts.Contains(c.DistrictName.Trim().ToLowerInvariant())).ToList(), rows);

        foreach (var chunk in keys.Chunk(500))
        {
            var ids = chunk.Select(k => k.Key).ToList();
            var tracked = await db.Cities.Where(c => ids.Contains(c.Id)).ToListAsync();
            foreach (var city in tracked) city.CityKey = keys[city.Id];
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
        }

        logger.LogInformation("seed.citykeys.backfill {Matched} {Unmatched} (source=file)",
            keys.Count, cities.Count - keys.Count);
    }

    private static async Task<List<CitySeedRow>> LoadFileRowsAsync()
    {
        var asm = typeof(DataSeeder).Assembly;
        await using var stream = asm.GetManifestResourceStream("RentNearBy.Infrastructure.Data.cities.json");
        if (stream == null)
        {
            Console.WriteLine("[DataSeeder] cities.json resource not found.");
            return new List<CitySeedRow>();
        }

        var records = await JsonSerializer.DeserializeAsync<List<CitySeedRecord?>>(stream, CaseInsensitiveJson);
        return RowsFromFile(records ?? new List<CitySeedRecord?>());
    }

    // Key = file index counted before any skip.
    internal static List<CitySeedRow> RowsFromFile(IReadOnlyList<CitySeedRecord?> records)
    {
        var rows = new List<CitySeedRow>(records.Count);
        for (var i = 0; i < records.Count; i++)
        {
            var r = records[i];
            if (r == null) continue;
            rows.Add(new CitySeedRow(CityKeys.FromIndex(i), r.Name, r.DistrictName, r.Latitude, r.Longitude, SeedStatusOk));
        }
        return rows;
    }

    internal static string MatchKey(string districtName, string name)
        => $"{districtName.Trim().ToLowerInvariant()}|{name.Trim().ToLowerInvariant()}";

    // City id -> key. First row wins per match key; a match key held by more than one local city is left unmatched.
    internal static Dictionary<Guid, long> MatchCityKeys(
        IReadOnlyList<(Guid Id, string DistrictName, string Name)> cities, IEnumerable<CitySeedRow> rows)
    {
        var byKey = cities
            .Where(c => !string.IsNullOrWhiteSpace(c.DistrictName) && !string.IsNullOrWhiteSpace(c.Name))
            .GroupBy(c => MatchKey(c.DistrictName, c.Name))
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.First().Id);

        var result = new Dictionary<Guid, long>();
        var usedKeys = new HashSet<long>();
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.DistrictName) || string.IsNullOrWhiteSpace(row.Name)) continue;
            if (!byKey.TryGetValue(MatchKey(row.DistrictName, row.Name), out var cityId)) continue;
            if (result.ContainsKey(cityId) || !usedKeys.Add(row.CityKey)) continue;
            result[cityId] = row.CityKey;
        }
        return result;
    }

    private static async Task SeedAdminsAsync(ApplicationDbContext db)
    {
        var admins = new[]
        {
            new { Email = "developerbymistake@gmail.com",    Phone = "9720565640", Hash = "$2a$12$Gabyh5O/zi1Q7kMhPhLThOwR5pcJEoV7/dbMAFD6CZnd8TTbTf.Bi" },
            new { Email = "devendrasinghphartyal@gmail.com", Phone = "7060023511", Hash = "$2a$12$.p82GvGMMlYPDDpj0Uyse.O2zIY/yG3HH25oU3ylUMcVkFTLVxZCq" },
        };

        var changed = false;
        foreach (var a in admins)
        {
            if (!await db.Admins.AnyAsync(x => x.Email == a.Email))
            {
                db.Admins.Add(new Admin
                {
                    Id = Guid.NewGuid(),
                    Email = a.Email,
                    PasswordHash = a.Hash,
                    PhoneNumber = a.Phone,
                    Name = "Admin",
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                });
                changed = true;
            }
        }

        if (changed) await db.SaveChangesAsync();
    }
}
