using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Models;
using System.Text.Json;

namespace RentNearBy.Infrastructure.Data;

public static class DataSeeder
{
    private sealed record DistrictSeedRecord(string Name, string StateName, string Boundary);
    private sealed record CitySeedRecord(string Name, string DistrictName, double Latitude, double Longitude);
    private sealed record InquiryHistorySeed(string Status, Guid? AdminId, DateTime CreatedAt, string? Note);
    private sealed record InquirySeed(
        Guid Id, int ServiceIdx, int PackageIdx, int? AgentIdx,
        string FullName, string Mobile, string? Email, DateTime? Trip, int? People, string? Message,
        string Status, InquiryHistorySeed[] History);

    private static readonly JsonSerializerOptions CaseInsensitiveJson = new() { PropertyNameCaseInsensitive = true };
    private static readonly WKTReader WktReader = new();

    public static async Task SeedAsync(ApplicationDbContext db)
    {
        await SeedRoomTypesAsync(db);
        await SeedPlotTypesAsync(db);
        await SeedReportReasonsAsync(db);
        await SeedQuestionTemplatesAsync(db);
        await SeedCoinFeaturesAsync(db);
        await SeedCoinPlansAsync(db);
        await SeedCoinPacksAsync(db);
        await SeedDistrictsAsync(db);
        await SeedCitiesAsync(db);
        await SeedListingLimitSettingsAsync(db);
        await SeedCouponsAsync(db);
        await SeedAdminsAsync(db);

        // Local Services Marketplace / Expert Consultations catalog — Explore Uttarakhand (Travel) +
        // Expert Consultations (Insurance/Yoga/Diet/Finance), sharing the same Section->Category->
        // Service->Package engine. Order matters: each method below FK-references rows created by an
        // earlier one via the deterministic ServiceCatalogId() ids, not a DB round-trip.
        await SeedServiceSectionsAsync(db);
        await SeedServiceCategoriesAsync(db);
        await SeedInclusionsAsync(db);
        await SeedServicesAsync(db);
        await SeedServicePackagesAsync(db);
        await SeedPackageInclusionsAsync(db);
        await SeedAgentsAsync(db);
        await SeedAgentServiceCategoriesAsync(db);
        await SeedSampleInquiriesAsync(db);
    }

    // Deterministic per-entity-type GUID, matching SeedQuestionTemplatesAsync's/SeedPlotTypesAsync's
    // Guid.Parse("<prefix>-...") style — lets a later seed method in this file (Packages -> Services,
    // Inquiries -> Packages/Agents) reference an earlier row's Id without a DB round-trip.
    // Prefixes used: e1=ServiceSection, e2=ServiceCategory, e3=Service, e4=ServicePackage,
    // e5=Inclusion, e6=Agent, e7=test consumer User, e8=Inquiry.
    private static Guid ServiceCatalogId(string prefix, int n) => Guid.Parse($"{prefix}-0000-0000-0000-{n:D12}");

    private static async Task SeedCouponsAsync(ApplicationDbContext db)
    {
        if (await db.Coupons.AnyAsync(c => c.Id == WellKnownCoupons.WelcomeSignupCouponId)) return;

        db.Coupons.Add(new Coupon
        {
            Id = WellKnownCoupons.WelcomeSignupCouponId,
            Code = null,
            CoinValue = 100,
            TriggerType = WellKnownCoupons.WelcomeSignupTrigger,
            PerUserLimit = 1,
            MaxTotalRedemptions = null,
            CurrentRedemptions = 0,
            ValidFrom = DateTime.UtcNow,
            ValidUntil = null,
            Status = CouponStatuses.Active,
            CreatedBy = null,
            CampaignLabel = "Welcome Bonus",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedListingLimitSettingsAsync(ApplicationDbContext db)
    {
        if (await db.ListingLimitSettings.AnyAsync()) return;

        db.ListingLimitSettings.AddRange(
            new ListingLimitSetting { Id = Guid.NewGuid(), ListingKind = ListingKinds.Room, MaxListings = 3, UpdatedAt = DateTime.UtcNow },
            new ListingLimitSetting { Id = Guid.NewGuid(), ListingKind = ListingKinds.Plot, MaxListings = 3, UpdatedAt = DateTime.UtcNow }
        );
        await db.SaveChangesAsync();
    }

    // Seed-only catalog of "what coins can be spent on" — Room/Plot Go-Live today, future coin-gated
    // features (contact reveal, chat, etc.) later, each as a new row here, never a schema change.
    private static async Task SeedCoinFeaturesAsync(ApplicationDbContext db)
    {
        if (await db.CoinFeatures.AnyAsync()) return;

        db.CoinFeatures.AddRange(
            new CoinFeature { Id = Guid.NewGuid(), Key = CoinFeatureKeys.RoomGoLive, DisplayName = "Room Go-Live", QuotaUnitLabel = "rooms", IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new CoinFeature { Id = Guid.NewGuid(), Key = CoinFeatureKeys.PlotGoLive, DisplayName = "Plot Go-Live", QuotaUnitLabel = "plots", IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow }
        );
        await db.SaveChangesAsync();
    }

    // Real coin-priced tiers matching the approved coin-economy mockups exactly (Basic/Standard/Premium,
    // Standard marked featured/"popular") — replaces the old RoomPlan/PlotPlan seed, which left stale
    // rupee-shaped placeholder values (Days=5/30, Price=99/199) behind after the coin-economy rework and
    // never seeded a third/Premium tier at all. Room and Plot use identical numbers, matching this
    // seeder's own prior precedent of equal values across both kinds.
    private static async Task SeedCoinPlansAsync(ApplicationDbContext db)
    {
        if (await db.CoinPlans.AnyAsync()) return;

        CoinPlan Make(string featureKey, string type, int days, int quota, int coins, bool featured) => new()
        {
            Id = Guid.NewGuid(), FeatureKey = featureKey, PlanType = type, Days = days, Quota = quota,
            Price = coins, DiscountPercent = 0, OriginalPrice = coins, IsFeatured = featured,
            IsEnabled = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };

        db.CoinPlans.AddRange(
            Make(CoinFeatureKeys.RoomGoLive, CoinPlanTypes.Basic,    days: 15, quota: 1, coins: 99,  featured: false),
            Make(CoinFeatureKeys.RoomGoLive, CoinPlanTypes.Standard, days: 30, quota: 2, coins: 299, featured: true),
            Make(CoinFeatureKeys.RoomGoLive, CoinPlanTypes.Premium,  days: 60, quota: 3, coins: 499, featured: false),
            Make(CoinFeatureKeys.PlotGoLive, CoinPlanTypes.Basic,    days: 15, quota: 1, coins: 99,  featured: false),
            Make(CoinFeatureKeys.PlotGoLive, CoinPlanTypes.Standard, days: 30, quota: 2, coins: 299, featured: true),
            Make(CoinFeatureKeys.PlotGoLive, CoinPlanTypes.Premium,  days: 60, quota: 3, coins: 499, featured: false)
        );
        await db.SaveChangesAsync();
    }

    // Coin packs (buy-coins tiers) — previously never seeded anywhere; the only INSERT path was the
    // admin app's create form, so GET /coin-packs/ had only ever returned an empty array on every real
    // deployment. Matches the approved mockups' Starter/Popular/Mega numbers exactly.
    private static async Task SeedCoinPacksAsync(ApplicationDbContext db)
    {
        if (await db.CoinPacks.AnyAsync()) return;

        CoinPack Make(int coins, int bonus, int priceInr, int sortOrder, bool featured) => new()
        {
            Id = Guid.NewGuid(), Coins = coins, BonusCoins = bonus, PriceInr = priceInr,
            IsEnabled = true, SortOrder = sortOrder, IsFeatured = featured,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };

        db.CoinPacks.AddRange(
            Make(coins: 100, bonus: 0,   priceInr: 99,  sortOrder: 1, featured: false), // Starter
            Make(coins: 300, bonus: 30,  priceInr: 299, sortOrder: 2, featured: true),  // Popular / Best Value
            Make(coins: 500, bonus: 50,  priceInr: 399, sortOrder: 3, featured: false)  // Mega
        );
        await db.SaveChangesAsync();
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
            new RoomType { Id = Guid.NewGuid(), Name = "Shop",   SortOrder = 6, Description = "Commercial shop or retail space",        CreatedAt = DateTime.UtcNow },
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

    private static async Task SeedReportReasonsAsync(ApplicationDbContext db)
    {
        if (await db.ReportReasons.AnyAsync()) return;

        var reasons = new[]
        {
            new ReportReason { Id = Guid.Parse("c1000000-0000-0000-0000-000000000001"), Name = "Incorrect information", SortOrder = 1, Description = "Price, location or photos don't match the actual property", CreatedAt = DateTime.UtcNow },
            new ReportReason { Id = Guid.Parse("c1000000-0000-0000-0000-000000000002"), Name = "Offensive content",     SortOrder = 2, Description = "Contains nudity, abusive language or hate speech",          CreatedAt = DateTime.UtcNow },
        };

        db.ReportReasons.AddRange(reasons);
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
        };

        var toAdd = templates.Where(t => !existingKeys.Contains(t.Key)).ToList();
        if (toAdd.Count == 0) return;

        db.QuestionTemplates.AddRange(toAdd);
        await db.SaveChangesAsync();
    }

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
            foreach (var record in records)
            {
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
                    Id = Guid.NewGuid(),
                    Name = record.Name.Trim(),
                    StateName = record.StateName.Trim(),
                    IsActive = true,
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

    private static async Task SeedCitiesAsync(ApplicationDbContext db)
    {
        if (await db.Cities.AnyAsync()) return;

        // Load only Id + Name into memory — avoids fetching geometry/boundary columns for 756 rows.
        // GroupBy in C# instead of EF Core (EF cannot translate g.ToList() to SQL).
        var allDistricts = await db.Districts
            .AsNoTracking()
            .Select(d => new { d.Id, d.Name })
            .ToListAsync();

        var districtGroups = allDistricts
            .GroupBy(d => d.Name.ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.ToList());

        var unambiguousLookup = districtGroups
            .Where(kv => kv.Value.Count == 1)
            .ToDictionary(kv => kv.Key, kv => kv.Value[0].Id);

        var ambiguousNames = districtGroups
            .Where(kv => kv.Value.Count > 1)
            .Select(kv => kv.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (ambiguousNames.Count > 0)
            Console.WriteLine($"[DataSeeder] {ambiguousNames.Count} district name(s) are ambiguous (same name, different states) — cities for these will be skipped.");

        if (unambiguousLookup.Count == 0)
        {
            Console.WriteLine("[DataSeeder] No districts found — skipping city seeding.");
            return;
        }

        var asm = typeof(DataSeeder).Assembly;
        await using var stream = asm.GetManifestResourceStream("RentNearBy.Infrastructure.Data.cities.json");
        if (stream == null)
        {
            Console.WriteLine("[DataSeeder] cities.json resource not found — skipping city seeding.");
            return;
        }

        var records = await JsonSerializer.DeserializeAsync<List<CitySeedRecord>>(stream, CaseInsensitiveJson);
        if (records == null || records.Count == 0)
        {
            Console.WriteLine("[DataSeeder] cities.json is empty — skipping city seeding.");
            return;
        }

        Console.WriteLine($"[DataSeeder] Seeding {records.Count} cities...");

        var batch = new List<City>(500);
        var seenCities = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // key: "{districtId}|{cityName}"
        var seeded = 0;
        var skippedNoDistrict = 0;
        var skippedAmbiguous = 0;
        var skippedDuplicate = 0;
        var batchCount = 0;

        db.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            foreach (var record in records)
            {
                if (string.IsNullOrWhiteSpace(record.Name) || string.IsNullOrWhiteSpace(record.DistrictName))
                    continue;

                var districtKey = record.DistrictName.ToLowerInvariant();

                if (ambiguousNames.Contains(districtKey))
                {
                    skippedAmbiguous++;
                    continue;
                }

                if (!unambiguousLookup.TryGetValue(districtKey, out var districtId))
                {
                    skippedNoDistrict++;
                    continue;
                }

                var cityKey = $"{districtId}|{record.Name.Trim()}";
                if (!seenCities.Add(cityKey))
                {
                    skippedDuplicate++;
                    continue;
                }

                batch.Add(new City
                {
                    Id = Guid.NewGuid(),
                    DistrictId = districtId,
                    Name = record.Name.Trim(),
                    Latitude = (decimal)record.Latitude,
                    Longitude = (decimal)record.Longitude,
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

        Console.WriteLine($"[DataSeeder] City seeding complete. Seeded: {seeded}, Skipped (no district): {skippedNoDistrict}, Skipped (ambiguous district): {skippedAmbiguous}, Skipped (duplicate): {skippedDuplicate}");
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

    // ── Local Services Marketplace / Expert Consultations ──────────────────────────────────────────

    private static async Task SeedServiceSectionsAsync(ApplicationDbContext db)
    {
        if (await db.ServiceSections.AnyAsync()) return;

        var now = DateTime.UtcNow;
        db.ServiceSections.AddRange(
            new ServiceSection { Id = ServiceCatalogId("e1000000", 1), Name = "Explore Uttarakhand", IconName = "map", SortOrder = 1, IsActive = true, CreatedAt = now },
            new ServiceSection { Id = ServiceCatalogId("e1000000", 2), Name = "Expert Consultations", IconName = "briefcase", SortOrder = 2, IsActive = true, CreatedAt = now }
        );
        await db.SaveChangesAsync();
    }

    private static async Task SeedServiceCategoriesAsync(ApplicationDbContext db)
    {
        if (await db.ServiceCategories.AnyAsync()) return;

        var explore = ServiceCatalogId("e1000000", 1);
        var consult = ServiceCatalogId("e1000000", 2);

        // (index, name, icon, sectionId) — index also becomes the matching Service's index in
        // SeedServicesAsync below: this initial catalog is 1:1 Category<->Service (9 Travel + 5
        // Consultation = 14 of each); the schema allows a Category to hold more than one Service later.
        var categories = new (int Index, string Name, string Icon, Guid SectionId)[]
        {
            (1,  "Char Dham Yatra",        "route_square",  explore),
            (2,  "Destination Wedding",    "heart",         explore),
            (3,  "Tour & Travel Packages", "airplane",      explore),
            (4,  "Taxi Booking",           "car",           explore),
            (5,  "Camping & Adventure",    "tree",          explore),
            (6,  "Photographer & Video",   "camera",        explore),
            (7,  "Homestay & Resort",      "building",      explore),
            (8,  "Bike on Rent",           "gas_station",   explore),
            (9,  "Event Planner",          "calendar",      explore),
            (10, "Health Insurance",       "shield_tick",   consult),
            (11, "Term Insurance",         "security_safe", consult),
            (12, "Yoga",                   "activity",      consult),
            (13, "Diet Plans",             "weight",        consult),
            (14, "Financial Planning",     "chart",         consult),
        };

        var now = DateTime.UtcNow;
        db.ServiceCategories.AddRange(categories.Select(c => new ServiceCategory
        {
            Id = ServiceCatalogId("e2000000", c.Index),
            ServiceSectionId = c.SectionId,
            Name = c.Name,
            IconName = c.Icon,
            SortOrder = c.Index,
            IsActive = true,
            CreatedAt = now,
        }));
        await db.SaveChangesAsync();
    }

    private static async Task SeedInclusionsAsync(ApplicationDbContext db)
    {
        if (await db.Inclusions.AnyAsync()) return;

        var inclusions = new (int Index, string Name, string Icon)[]
        {
            (1,  "Hotel Stay",       "building"),
            (2,  "Meals Included",   "cup"),
            (3,  "Local Transport",  "car"),
            (4,  "Tour Guide",       "profile_2user"),
            (5,  "Travel Insurance", "shield_tick"),
            (6,  "Sightseeing",      "route_square"),
            (7,  "Photography",      "camera"),
            (8,  "Entry Tickets",    "ticket"),
            (9,  "First Aid Kit",    "health"),
            (10, "WiFi Access",      "wifi"),
        };

        db.Inclusions.AddRange(inclusions.Select(i => new Inclusion
        {
            Id = ServiceCatalogId("e5000000", i.Index),
            Name = i.Name,
            IconName = i.Icon,
            SortOrder = i.Index,
            IsActive = true,
        }));
        await db.SaveChangesAsync();
    }

    private static async Task SeedServicesAsync(ApplicationDbContext db)
    {
        if (await db.Services.AnyAsync()) return;

        // (index, categoryIndex, name, icon, short, full, featured) — a Category may now hold several
        // Services (schema always supported this — see the comment on SeedServiceCategoriesAsync).
        // Categories whose old single Service's "packages" were actually distinct offerings (different
        // dhams, different tour itineraries, different taxi/vehicle/event types, different insurance
        // products) are split into one Service per real offering here, each getting its own genuine
        // price/duration plans in SeedServicePackagesAsync below. Categories where the old packages
        // were already true price/scope tiers of ONE offering (Destination Wedding, Homestay & Resort,
        // Photographer & Video) keep a single Service.
        var services = new (int Index, int CategoryIdx, string Name, string Icon, string Short, string Full, bool Featured)[]
        {
            // Char Dham Yatra (category 1) — 4 individual dhams + 1 all-4 combo
            (1, 1, "Badrinath Yatra", "route_square",
                "Guided pilgrimage to Badrinath, by road or helicopter.",
                "Pilgrimage packages to Badrinath Dham — travel by road with an experienced guide, or fly in by helicopter for a faster trip. Hotel stay and meals included on the road package.",
                true),
            (2, 1, "Kedarnath Yatra", "route_square",
                "Guided pilgrimage to Kedarnath, by road/trek or helicopter.",
                "Pilgrimage packages to Kedarnath Dham — trek/road journey with an experienced guide, or fly in by helicopter to skip the trek. Hotel stay and meals included on the road package.",
                true),
            (3, 1, "Yamunotri Yatra", "route_square",
                "Guided pilgrimage to Yamunotri, by road/trek or helicopter.",
                "Pilgrimage packages to Yamunotri Dham — trek/road journey with an experienced guide, or fly in by helicopter for a quicker visit.",
                false),
            (4, 1, "Gangotri Yatra", "route_square",
                "Guided pilgrimage to Gangotri, by road or helicopter.",
                "Pilgrimage packages to Gangotri Dham — road journey with an experienced guide, or fly in by helicopter for a faster trip.",
                false),
            (5, 1, "Char Dham Yatra (All 4 Combo)", "route_square",
                "Combined pilgrimage covering all 4 dhams — Kedarnath, Badrinath, Gangotri and Yamunotri.",
                "Complete Char Dham Yatra packages covering all four dhams together, with hotel stays, meals, local transport and an experienced tour guide. Choose from Do Dham (2 dhams), full Char Dham (all 4) or an all-helicopter combo.",
                true),

            // Destination Wedding (category 2) — unchanged, single Service with tiered venue packages
            (6, 2, "Destination Wedding", "heart",
                "Riverside, hillside and palace wedding venues across Uttarakhand.",
                "Plan your dream destination wedding in the hills — riverside resorts, heritage palaces and intimate hillside venues, with catering, decor and photography coordinated end-to-end.",
                false),

            // Tour & Travel Packages (category 3) — one Service per itinerary
            (7, 3, "Custom Uttarakhand Circuit", "airplane",
                "Build-your-own multi-day circuit across Uttarakhand's hill stations.",
                "A fully customisable multi-day tour across Uttarakhand — pick your own mix of hill stations. Pricing starts at a base and scales with your chosen plan.",
                false),
            (8, 3, "Nainital-Mussoorie Duo Tour", "airplane",
                "5D/4N covering both Nainital and Mussoorie.",
                "A 5-day, 4-night tour covering both Nainital and Mussoorie's top sightseeing spots, with hotel stays and local transport.",
                true),
            (9, 3, "Kumaon Hills Explorer", "airplane",
                "6D/5N exploring the Kumaon hills.",
                "A 6-day, 5-night circuit through the Kumaon region's hill towns and viewpoints, with hotel stays, meals and a tour guide.",
                false),
            (10, 3, "Garhwal Discovery Tour", "airplane",
                "7D/6N exploring the Garhwal hills.",
                "A 7-day, 6-night circuit through the Garhwal region, covering major hill stations, sightseeing and entry tickets.",
                false),

            // Taxi Booking (category 4) — local city taxi vs airport transfer are genuinely different services
            (11, 4, "Local City Taxi", "car",
                "Half-day and full-day local city taxis.",
                "Book a local city taxi for sightseeing and errands — available as a half-day or full-day hire.",
                true),
            (12, 4, "Airport Transfer", "car",
                "One-way airport pickup/drop.",
                "Reliable one-way airport transfer service, booked in advance for a smooth pickup or drop.",
                false),

            // Camping & Adventure (category 5) — camping, trekking-combo and adventure-sports are distinct
            (13, 5, "Riverside Camping", "tree",
                "Riverside camping with bonfire, for couples, friends or families.",
                "Riverside camping packages with bonfire and overnight stay — available as a standard 2D/1N trip or a family camping weekend.",
                true),
            (14, 5, "Trekking & Camping Combo", "tree",
                "3D/2N combining a guided trek with camping.",
                "A 3-day, 2-night combo of a guided trek and riverside camping, with a local expert guide.",
                false),
            (15, 5, "Adventure Sports", "tree",
                "Single-day river rafting and adventure sports.",
                "A single-day adventure sports package — river rafting, rappelling and more, guided by local experts with first-aid support on-site.",
                false),

            // Photographer & Video (category 6) — unchanged, single Service with scope tiers
            (16, 6, "Photographer & Video", "camera",
                "Professional photography and videography for events and weddings.",
                "Professional photographers and videographers for weddings, pre-wedding shoots and events, with half-day, full-day and full cinematography packages.",
                false),

            // Homestay & Resort (category 7) — unchanged, single Service with a budget->luxury price ladder
            (17, 7, "Homestay & Resort", "building",
                "Budget homestays to luxury hillside resorts.",
                "Curated stays across Uttarakhand — cozy family-run homestays, riverside resorts and luxury hillside properties, with meals and WiFi included on select packages.",
                false),

            // Bike on Rent (category 8) — scooter vs motorcycle are different vehicle categories
            (18, 8, "Scooter Rental", "gas_station",
                "Self-drive scooters (Activa or similar), by the day.",
                "Rent a scooter for self-drive exploration of the hills — daily rental.",
                false),
            (19, 8, "Motorcycle Rental", "gas_station",
                "Self-drive motorcycles (Royal Enfield), by the day or week.",
                "Rent a Royal Enfield motorcycle for self-drive exploration of the hills — daily or weekly rental packages available.",
                true),

            // Event Planner (category 9) — one Service per event type
            (20, 9, "Birthday & Small Event Planning", "calendar",
                "End-to-end planning for birthdays and small events.",
                "Full-service planning for birthdays and small get-togethers — venue, catering, decor and logistics handled by a dedicated planner.",
                false),
            (21, 9, "Anniversary & Reception Planning", "calendar",
                "End-to-end planning for anniversaries and receptions.",
                "Full-service planning for anniversary parties and receptions — venue, catering, decor and logistics handled by a dedicated planner.",
                false),
            (22, 9, "Corporate Event Planning", "calendar",
                "End-to-end planning for corporate events.",
                "Full-service planning for corporate events and offsites — venue, catering and logistics handled by a dedicated planner.",
                false),
            (23, 9, "Wedding Event Management", "calendar",
                "End-to-end planning for full wedding events.",
                "Complete wedding event management — venue, catering, decor and logistics handled end-to-end by a dedicated planner.",
                true),

            // Health Insurance (category 10) — each product is its own Service, priced by custom quote
            (24, 10, "Individual Health Cover", "shield_tick",
                "Health insurance for a single individual.",
                "Individual health insurance cover — get a custom quote based on your age and health needs.",
                false),
            (25, 10, "Family Floater Plan", "shield_tick",
                "One health cover shared across the whole family.",
                "A single health insurance policy covering the entire family — get a custom quote based on your family's needs.",
                true),
            (26, 10, "Senior Citizen Health Plan", "shield_tick",
                "Health cover designed for senior citizens.",
                "Health insurance designed for senior citizens' specific coverage needs — get a custom quote.",
                false),
            (27, 10, "Critical Illness Health Cover", "shield_tick",
                "Lump-sum cover on diagnosis of a critical illness.",
                "Critical illness health cover paying a lump sum on diagnosis of a covered condition — get a custom quote.",
                false),

            // Term Insurance (category 11)
            (28, 11, "Basic Term Plan", "security_safe",
                "Simple term life cover.",
                "A straightforward term life insurance plan — get a custom quote matched to your income and family's needs.",
                false),
            (29, 11, "Term Plan with Critical Illness Cover", "security_safe",
                "Term life cover with a critical illness rider.",
                "Term life insurance with an added critical illness rider — get a custom quote.",
                true),
            (30, 11, "Term Plan with Return of Premium", "security_safe",
                "Term life cover that returns your premiums at maturity.",
                "Term life insurance with a return-of-premium option — get a custom quote.",
                false),

            // Yoga (category 12) — one Service per session type, each with real low/high-budget plans
            (31, 12, "1-on-1 Yoga Session", "activity",
                "Private one-on-one yoga sessions.",
                "Private yoga sessions with an instructor, one-on-one — choose a regular session or one with a certified instructor.",
                false),
            (32, 12, "Group Yoga Classes", "activity",
                "Monthly group yoga classes.",
                "Monthly group yoga classes — choose a regular group batch or a smaller premium-instructor batch.",
                true),
            (33, 12, "Yoga Retreat", "activity",
                "Multi-day yoga retreats in the hills.",
                "Multi-day yoga retreats in the hills — choose a weekend retreat or a full 7-day retreat.",
                false),
            (34, 12, "Corporate Yoga Workshop", "activity",
                "Yoga workshops for corporate teams.",
                "Yoga workshops for corporate teams — a single session or an ongoing monthly program.",
                false),

            // Diet Plans (category 13)
            (35, 13, "Weight Loss Diet Plan", "weight",
                "Personalised weight-loss diet plan.",
                "A personalised weight-loss diet plan from a certified nutritionist, with ongoing consultation support — get a custom quote.",
                true),
            (36, 13, "Weight Gain Diet Plan", "weight",
                "Personalised weight-gain diet plan.",
                "A personalised weight-gain diet plan from a certified nutritionist, with ongoing consultation support — get a custom quote.",
                false),
            (37, 13, "Diabetic-Friendly Diet Plan", "weight",
                "Personalised diabetic-friendly diet plan.",
                "A personalised diabetic-friendly diet plan from a certified nutritionist, with ongoing consultation support — get a custom quote.",
                false),

            // Financial Planning (category 14)
            (38, 14, "Personal Financial Planning", "chart",
                "One-on-one personal financial planning.",
                "A one-on-one consultation covering your personal financial planning goals — get a custom quote from our advisor.",
                true),
            (39, 14, "Retirement Planning", "chart",
                "One-on-one retirement planning.",
                "A one-on-one consultation covering your retirement planning goals — get a custom quote from our advisor.",
                false),
            (40, 14, "Investment Portfolio Review", "chart",
                "One-on-one investment portfolio review.",
                "A one-on-one review of your investment portfolio with our advisor — get a custom quote.",
                false),
        };

        var now = DateTime.UtcNow;
        db.Services.AddRange(services.Select(s => new Service
        {
            Id = ServiceCatalogId("e3000000", s.Index),
            ServiceCategoryId = ServiceCatalogId("e2000000", s.CategoryIdx),
            Name = s.Name,
            IconName = s.Icon,
            ShortDescription = s.Short,
            FullDescription = s.Full,
            CoverPhotoUrl = string.Empty,
            CoverPhotoFilePath = string.Empty,
            SortOrder = s.Index,
            IsFeatured = s.Featured,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        }));
        await db.SaveChangesAsync();
    }

    private static async Task SeedServicePackagesAsync(ApplicationDbContext db)
    {
        if (await db.ServicePackages.AnyAsync()) return;

        // (index, serviceIndex, name, price, originalPrice, discountPercent, isStartingAtPrice,
        //  durationDays, durationNights, priceUnit, sortOrder, isFeatured)
        // Price=null on every Consultation-type plan renders "Get Custom Quote". IsStartingAtPrice is
        // true ONLY on the Tour & Travel Packages itinerary Services, per the confirmed design decision.
        // Plan names are simple and concrete (by travel mode / duration / session type), never abstract
        // tier labels like "Standard/Deluxe/Premium".
        var packages = new (int Index, int ServiceIdx, string Name, int? Price, int? OriginalPrice, int? DiscountPercent,
            bool StartingAt, int? Days, int? Nights, string? Unit, int SortOrder, bool Featured)[]
        {
            // Badrinath Yatra (service 1)
            (1, 1, "Badrinath Yatra by Road - 3D/2N", 7499, null, null, false, 3, 2, "per person", 1, true),
            (2, 1, "Badrinath Yatra by Helicopter - 1D", 18999, null, null, false, 1, 0, "per person", 2, false),

            // Kedarnath Yatra (service 2)
            (3, 2, "Kedarnath Yatra by Road/Trek - 4D/3N", 8999, null, null, false, 4, 3, "per person", 1, true),
            (4, 2, "Kedarnath Yatra by Helicopter - 1D", 15999, null, null, false, 1, 0, "per person", 2, false),

            // Yamunotri Yatra (service 3)
            (5, 3, "Yamunotri Yatra by Road/Trek - 2D/1N", 4999, null, null, false, 2, 1, "per person", 1, true),
            (6, 3, "Yamunotri Yatra by Helicopter - 1D", 12999, null, null, false, 1, 0, "per person", 2, false),

            // Gangotri Yatra (service 4)
            (7, 4, "Gangotri Yatra by Road - 2D/1N", 5499, null, null, false, 2, 1, "per person", 1, true),
            (8, 4, "Gangotri Yatra by Helicopter - 1D", 13999, null, null, false, 1, 0, "per person", 2, false),

            // Char Dham Yatra (All 4 Combo) (service 5)
            (9, 5, "Do Dham Yatra (Kedarnath-Badrinath) - 6D/5N", 14999, 17999, 17, false, 6, 5, "per person", 1, true),
            (10, 5, "Char Dham Yatra Complete - 11D/10N", 27999, 32999, 15, false, 11, 10, "per person", 2, false),
            (11, 5, "Helicopter Char Dham Yatra - 5D/4N", 45999, 52999, 13, false, 5, 4, "per person", 3, false),

            // Destination Wedding (service 6) — unchanged
            (12, 6, "Intimate Hill Wedding Package", 149999, null, null, false, 2, 1, "per event", 1, false),
            (13, 6, "Riverside Wedding Package", 299999, null, null, false, 3, 2, "per event", 2, true),
            (14, 6, "Royal Palace Wedding Package", 599999, 699999, 14, false, 4, 3, "per event", 3, false),
            (15, 6, "Beach-style Riverside Wedding", 349999, 399999, 12, false, 3, 2, "per event", 4, false),

            // Custom Uttarakhand Circuit (service 7)
            (16, 7, "Custom Uttarakhand Circuit", 6999, null, null, true, 3, 2, "per person", 1, false),

            // Nainital-Mussoorie Duo Tour (service 8)
            (17, 8, "Nainital-Mussoorie Duo Tour", 8999, null, null, true, 5, 4, "per person", 1, true),

            // Kumaon Hills Explorer (service 9)
            (18, 9, "Kumaon Hills Explorer", 11999, 13999, 14, true, 6, 5, "per person", 1, false),

            // Garhwal Discovery Tour (service 10)
            (19, 10, "Garhwal Discovery Tour", 15999, null, null, true, 7, 6, "per person", 1, false),

            // Local City Taxi (service 11)
            (20, 11, "Local City Taxi - Half Day", 1499, null, null, false, null, null, "per day", 1, false),
            (21, 11, "Local City Taxi - Full Day", 2499, null, null, false, null, null, "per day", 2, true),

            // Airport Transfer (service 12)
            (22, 12, "Airport Transfer (One-way)", 1999, null, null, false, null, null, "per trip", 1, false),

            // Riverside Camping (service 13)
            (23, 13, "Riverside Camping - 2D/1N", 2999, 3499, 14, false, 2, 1, "per person", 1, true),
            (24, 13, "Family Camping Weekend - 2D/1N", 3499, null, null, false, 2, 1, "per person", 2, false),

            // Trekking & Camping Combo (service 14)
            (25, 14, "Trekking & Camping Combo - 3D/2N", 5999, null, null, false, 3, 2, "per person", 1, false),

            // Adventure Sports (service 15)
            (26, 15, "Adventure Sports Day Package", 1999, null, null, false, 1, 0, "per person", 1, false),

            // Photographer & Video (service 16) — unchanged
            (27, 16, "Half-Day Photography", 7999, null, null, false, null, null, "per event", 1, false),
            (28, 16, "Full-Day Photo + Video", 15999, 17999, 11, false, null, null, "per event", 2, true),
            (29, 16, "Wedding Cinematography Package", 39999, null, null, false, null, null, "per event", 3, false),

            // Homestay & Resort (service 17) — unchanged
            (30, 17, "Budget Homestay", 1299, null, null, false, null, 1, "per night", 1, false),
            (31, 17, "Cozy Mountain Homestay", 1999, null, null, false, null, 1, "per night", 2, false),
            (32, 17, "Riverside Resort Stay", 4999, 5999, 17, false, null, 1, "per night", 3, true),
            (33, 17, "Luxury Hillside Resort", 8999, null, null, false, null, 1, "per night", 4, false),

            // Scooter Rental (service 18)
            (34, 18, "Scooty/Activa - Per Day", 699, null, null, false, null, null, "per day", 1, false),

            // Motorcycle Rental (service 19)
            (35, 19, "Royal Enfield - Per Day", 1499, null, null, false, null, null, "per day", 1, true),
            (36, 19, "Bike Rental - Weekly Package", 8999, 10499, 14, false, null, null, "per week", 2, false),

            // Birthday & Small Event Planning (service 20)
            (37, 20, "Birthday/Small Event Planning", 19999, null, null, false, null, null, "per event", 1, false),

            // Anniversary & Reception Planning (service 21)
            (38, 21, "Anniversary/Reception Planning", 29999, null, null, false, null, null, "per event", 1, false),

            // Corporate Event Planning (service 22)
            (39, 22, "Corporate Event Planning", 49999, null, null, false, null, null, "per event", 1, false),

            // Wedding Event Management (service 23)
            (40, 23, "Full Wedding Event Management", 99999, 119999, 17, false, null, null, "per event", 1, true),

            // Individual Health Cover (service 24) — Price=null: "Get Custom Quote"
            (41, 24, "Individual Health Cover", null, null, null, false, null, null, null, 1, false),

            // Family Floater Plan (service 25)
            (42, 25, "Family Floater Plan", null, null, null, false, null, null, null, 1, true),

            // Senior Citizen Health Plan (service 26)
            (43, 26, "Senior Citizen Health Plan", null, null, null, false, null, null, null, 1, false),

            // Critical Illness Health Cover (service 27)
            (44, 27, "Critical Illness Health Cover", null, null, null, false, null, null, null, 1, false),

            // Basic Term Plan (service 28)
            (45, 28, "Basic Term Plan", null, null, null, false, null, null, null, 1, false),

            // Term Plan with Critical Illness Cover (service 29)
            (46, 29, "Term Plan with Critical Illness Cover", null, null, null, false, null, null, null, 1, true),

            // Term Plan with Return of Premium (service 30)
            (47, 30, "Term Plan with Return of Premium", null, null, null, false, null, null, null, 1, false),

            // 1-on-1 Yoga Session (service 31) — real low/high-budget plans
            (48, 31, "Regular Session", 499, null, null, false, null, null, "per session", 1, false),
            (49, 31, "Session with Certified Instructor", 999, null, null, false, null, null, "per session", 2, true),

            // Group Yoga Classes (service 32)
            (50, 32, "Monthly - Group Batch", 1499, null, null, false, null, null, "per month", 1, true),
            (51, 32, "Monthly - Small Batch (Premium Instructor)", 2499, null, null, false, null, null, "per month", 2, false),

            // Yoga Retreat (service 33)
            (52, 33, "Weekend Retreat - 2D/1N", 7999, null, null, false, 2, 1, "per person", 1, true),
            (53, 33, "7-Day Retreat", 17999, null, null, false, 7, 6, "per person", 2, false),

            // Corporate Yoga Workshop (service 34)
            (54, 34, "Single Session Workshop", 9999, null, null, false, null, null, "per event", 1, false),
            (55, 34, "Monthly Corporate Program", 29999, null, null, false, null, null, "per month", 2, true),

            // Weight Loss Diet Plan (service 35)
            (56, 35, "Weight Loss Diet Plan", null, null, null, false, null, null, null, 1, true),

            // Weight Gain Diet Plan (service 36)
            (57, 36, "Weight Gain Diet Plan", null, null, null, false, null, null, null, 1, false),

            // Diabetic-Friendly Diet Plan (service 37)
            (58, 37, "Diabetic-Friendly Diet Plan", null, null, null, false, null, null, null, 1, false),

            // Personal Financial Planning (service 38)
            (59, 38, "Personal Financial Planning", null, null, null, false, null, null, null, 1, true),

            // Retirement Planning (service 39)
            (60, 39, "Retirement Planning", null, null, null, false, null, null, null, 1, false),

            // Investment Portfolio Review (service 40)
            (61, 40, "Investment Portfolio Review", null, null, null, false, null, null, null, 1, false),
        };

        var now = DateTime.UtcNow;
        db.ServicePackages.AddRange(packages.Select(p => new ServicePackage
        {
            Id = ServiceCatalogId("e4000000", p.Index),
            ServiceId = ServiceCatalogId("e3000000", p.ServiceIdx),
            Name = p.Name,
            Price = p.Price,
            OriginalPrice = p.OriginalPrice,
            DiscountPercent = p.DiscountPercent,
            IsStartingAtPrice = p.StartingAt,
            DurationDays = p.Days,
            DurationNights = p.Nights,
            PriceUnit = p.Unit,
            ThumbnailUrl = string.Empty,
            ThumbnailFilePath = string.Empty,
            SortOrder = p.SortOrder,
            IsFeatured = p.Featured,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        }));
        await db.SaveChangesAsync();
    }

    private static async Task SeedPackageInclusionsAsync(ApplicationDbContext db)
    {
        if (await db.PackageInclusions.AnyAsync()) return;

        // (packageIndex, inclusionIndices[]) — only a representative subset of Tourism packages get
        // inclusions wired up (Consultation packages have no physical "inclusions" concept). Indices
        // here match the restructured package list in SeedServicePackagesAsync above.
        var mappings = new (int PackageIdx, int[] InclusionIdxs)[]
        {
            (9,  new[] { 1, 2, 3, 5 }),         // Do Dham Yatra: Hotel, Meals, Transport, Travel Insurance
            (10, new[] { 1, 2, 3, 4, 5 }),      // Char Dham Complete: + Tour Guide
            (11, new[] { 1, 2, 4, 5, 9 }),      // Helicopter Char Dham: + First Aid Kit
            (13, new[] { 1, 2, 7 }),            // Riverside Wedding: Hotel, Meals, Photography
            (14, new[] { 1, 2, 3, 7 }),         // Royal Palace Wedding: + Local Transport
            (17, new[] { 1, 2, 3, 6 }),         // Nainital-Mussoorie Duo Tour: + Sightseeing
            (18, new[] { 1, 2, 4, 6 }),         // Kumaon Hills Explorer
            (19, new[] { 1, 2, 3, 6, 8 }),      // Garhwal Discovery Tour: + Entry Tickets
            (23, new[] { 2, 4, 9 }),            // Riverside Camping: Meals, Tour Guide, First Aid Kit
            (25, new[] { 2, 4, 8, 9 }),         // Trekking & Camping Combo: + Entry Tickets
            (32, new[] { 2, 10 }),              // Riverside Resort Stay: Meals, WiFi
            (33, new[] { 2, 3, 10 }),           // Luxury Hillside Resort: + Local Transport
        };

        foreach (var m in mappings)
        {
            var packageId = ServiceCatalogId("e4000000", m.PackageIdx);
            db.PackageInclusions.AddRange(m.InclusionIdxs.Select(i => new PackageInclusion
            {
                ServicePackageId = packageId,
                InclusionId = ServiceCatalogId("e5000000", i),
            }));
        }
        await db.SaveChangesAsync();
    }

    private static async Task SeedAgentsAsync(ApplicationDbContext db)
    {
        if (await db.Agents.AnyAsync()) return;

        var agents = new (int Index, string Name, string Phone, string WhatsApp)[]
        {
            (1, "Rajesh Bisht",       "9411100001", "9411100001"),
            (2, "Priya Negi",         "9411100002", "8888800002"),
            (3, "Amit Rawat",         "9411100003", "9411100003"),
            (4, "Sunita Joshi",       "9411100004", "9411100004"),
            (5, "Dr. Vikram Singh",   "9411100005", "8888800005"),
            (6, "Anjali Rana",        "9411100006", "9411100006"),
        };

        var now = DateTime.UtcNow;
        db.Agents.AddRange(agents.Select(a => new Agent
        {
            Id = ServiceCatalogId("e6000000", a.Index),
            Name = a.Name,
            Phone = a.Phone,
            WhatsAppNumber = a.WhatsApp,
            PhotoUrl = string.Empty,
            PhotoFilePath = string.Empty,
            IsActive = true,
            CreatedAt = now,
        }));
        await db.SaveChangesAsync();
    }

    private static async Task SeedAgentServiceCategoriesAsync(ApplicationDbContext db)
    {
        if (await db.AgentServiceCategories.AnyAsync()) return;

        // (agentIndex, categoryIndices[]) — several agents deliberately cover multiple categories,
        // matching the confirmed "one agent may serve multiple categories" design.
        var mappings = new (int AgentIdx, int[] CategoryIdxs)[]
        {
            (1, new[] { 1, 3, 5 }),   // Rajesh Bisht: Char Dham Yatra, Tour & Travel Packages, Camping & Adventure
            (2, new[] { 2, 6, 9 }),   // Priya Negi: Destination Wedding, Photographer & Video, Event Planner
            (3, new[] { 4, 8 }),      // Amit Rawat: Taxi Booking, Bike on Rent
            (4, new[] { 7 }),         // Sunita Joshi: Homestay & Resort
            (5, new[] { 10, 11, 14 }),// Dr. Vikram Singh: Health Insurance, Term Insurance, Financial Planning
            (6, new[] { 12, 13 }),    // Anjali Rana: Yoga, Diet Plans
        };

        foreach (var m in mappings)
        {
            var agentId = ServiceCatalogId("e6000000", m.AgentIdx);
            db.AgentServiceCategories.AddRange(m.CategoryIdxs.Select(c => new AgentServiceCategory
            {
                AgentId = agentId,
                ServiceCategoryId = ServiceCatalogId("e2000000", c),
            }));
        }
        await db.SaveChangesAsync();
    }

    // Sample Inquiries spanning all 5 InquiryStatuses states, for admin/consumer QA of the pipeline.
    // Inquiry.UserId is a mandatory FK and no other Seed method in this file seeds a consumer User (see
    // SeedAsync's call list) — checked explicitly, not assumed — so a single minimal test consumer is
    // created here rather than silently skipping this data or letting the FK insert fail.
    private static async Task SeedSampleInquiriesAsync(ApplicationDbContext db)
    {
        if (await db.Inquiries.AnyAsync()) return;

        var testUserId = ServiceCatalogId("e7000000", 1);
        if (!await db.Users.AnyAsync(u => u.Id == testUserId))
        {
            db.Users.Add(new User
            {
                Id = testUserId,
                PhoneNumber = "9999900001",
                Name = "Test Consumer",
                IsActive = true,
                IsPhoneVerified = true,
                HasUsedPhoneChange = false,
                IsContactVisible = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var adminIds = await db.Admins.OrderBy(a => a.Email).Select(a => a.Id).ToListAsync();
        Guid? admin1 = adminIds.ElementAtOrDefault(0) == Guid.Empty ? null : adminIds.ElementAtOrDefault(0);
        Guid? admin2 = adminIds.Count > 1 ? adminIds[1] : admin1;

        var now = DateTime.UtcNow;

        var seeds = new List<InquirySeed>
        {
            new(ServiceCatalogId("e8000000", 1), ServiceIdx: 5, PackageIdx: 9, AgentIdx: 1,
                FullName: "Anita Verma", Mobile: "9876500001", Email: "anita.verma@example.com",
                Trip: now.AddDays(45), People: 4,
                Message: "Planning a family trip for Do Dham Yatra in the first week of next month.",
                Status: InquiryStatuses.Confirmed,
                History:
                [
                    new(InquiryStatuses.Submitted, null,   now.AddDays(-10), null),
                    new(InquiryStatuses.Contacted, admin1, now.AddDays(-8),  null),
                    new(InquiryStatuses.Confirmed, admin1, now.AddDays(-5),  null),
                ]),

            new(ServiceCatalogId("e8000000", 2), ServiceIdx: 9, PackageIdx: 18, AgentIdx: 1,
                FullName: "Rohit Sharma", Mobile: "9876500002", Email: null,
                Trip: now.AddDays(20), People: 2,
                Message: "Interested in the Kumaon Hills Explorer package for a couple's trip.",
                Status: InquiryStatuses.Contacted,
                History:
                [
                    new(InquiryStatuses.Submitted, null,   now.AddDays(-6), null),
                    new(InquiryStatuses.Contacted, admin1, now.AddDays(-4), null),
                ]),

            new(ServiceCatalogId("e8000000", 3), ServiceIdx: 6, PackageIdx: 14, AgentIdx: null,
                FullName: "Kavita & Sameer", Mobile: "9876500003", Email: "kavita.sameer@example.com",
                Trip: now.AddDays(90), People: 150,
                Message: "Looking for a palace wedding venue for around 150 guests in November.",
                Status: InquiryStatuses.Submitted,
                History:
                [
                    new(InquiryStatuses.Submitted, null, now.AddDays(-1), null),
                ]),

            new(ServiceCatalogId("e8000000", 4), ServiceIdx: 12, PackageIdx: 22, AgentIdx: 3,
                FullName: "Manoj Bisht", Mobile: "9876500004", Email: null,
                Trip: now.AddDays(3), People: 1,
                Message: "Need airport pickup from Dehradun on arrival.",
                Status: InquiryStatuses.Confirmed,
                History:
                [
                    new(InquiryStatuses.Submitted, null,   now.AddDays(-3), null),
                    new(InquiryStatuses.Contacted, admin2, now.AddDays(-2), null),
                    new(InquiryStatuses.Confirmed, admin2, now.AddDays(-1), null),
                ]),

            new(ServiceCatalogId("e8000000", 5), ServiceIdx: 13, PackageIdx: 23, AgentIdx: 1,
                FullName: "Deepak Rana", Mobile: "9876500005", Email: null,
                Trip: now.AddDays(15), People: 6,
                Message: "Group camping trip for college friends.",
                Status: InquiryStatuses.Cancelled,
                History:
                [
                    new(InquiryStatuses.Submitted, null,   now.AddDays(-9), null),
                    new(InquiryStatuses.Contacted, admin1, now.AddDays(-7), null),
                    new(InquiryStatuses.Cancelled, admin1, now.AddDays(-6), "Customer cancelled due to a date conflict."),
                ]),

            new(ServiceCatalogId("e8000000", 6), ServiceIdx: 17, PackageIdx: 32, AgentIdx: null,
                FullName: "Neha Joshi", Mobile: "9876500006", Email: "neha.joshi@example.com",
                Trip: now.AddDays(10), People: 2,
                Message: null,
                Status: InquiryStatuses.Submitted,
                History:
                [
                    new(InquiryStatuses.Submitted, null, now.AddDays(-2), null),
                ]),

            new(ServiceCatalogId("e8000000", 7), ServiceIdx: 25, PackageIdx: 42, AgentIdx: 5,
                FullName: "Suresh Kumar", Mobile: "9876500007", Email: "suresh.kumar@example.com",
                Trip: null, People: 4,
                Message: "Looking for a family floater plan covering parents and 2 kids.",
                Status: InquiryStatuses.Contacted,
                History:
                [
                    new(InquiryStatuses.Submitted, null,   now.AddDays(-5), null),
                    new(InquiryStatuses.Contacted, admin2, now.AddDays(-3), null),
                ]),

            new(ServiceCatalogId("e8000000", 8), ServiceIdx: 28, PackageIdx: 45, AgentIdx: 5,
                FullName: "Ramesh Chandra", Mobile: "9876500008", Email: null,
                Trip: null, People: 1,
                Message: "Want term cover of 1 crore.",
                Status: InquiryStatuses.Rejected,
                History:
                [
                    new(InquiryStatuses.Submitted, null,   now.AddDays(-14), null),
                    new(InquiryStatuses.Contacted, admin2, now.AddDays(-12), null),
                    new(InquiryStatuses.Rejected,  admin2, now.AddDays(-10), "Customer did not meet minimum eligibility criteria."),
                ]),

            new(ServiceCatalogId("e8000000", 9), ServiceIdx: 32, PackageIdx: 50, AgentIdx: 6,
                FullName: "Pooja Mehta", Mobile: "9876500009", Email: "pooja.mehta@example.com",
                Trip: now.AddDays(7), People: 1,
                Message: "Want to join the monthly group classes.",
                Status: InquiryStatuses.Confirmed,
                History:
                [
                    new(InquiryStatuses.Submitted, null,   now.AddDays(-4), null),
                    new(InquiryStatuses.Contacted, admin1, now.AddDays(-3), null),
                    new(InquiryStatuses.Confirmed, admin1, now.AddDays(-2), null),
                ]),

            new(ServiceCatalogId("e8000000", 10), ServiceIdx: 35, PackageIdx: 56, AgentIdx: null,
                FullName: "Arjun Thapa", Mobile: "9876500010", Email: null,
                Trip: null, People: 1,
                Message: "Need a weight loss plan, vegetarian preferred.",
                Status: InquiryStatuses.Submitted,
                History:
                [
                    new(InquiryStatuses.Submitted, null, now, null),
                ]),
        };

        foreach (var s in seeds)
        {
            db.Inquiries.Add(new Inquiry
            {
                Id = s.Id,
                UserId = testUserId,
                ServiceId = ServiceCatalogId("e3000000", s.ServiceIdx),
                ServicePackageId = ServiceCatalogId("e4000000", s.PackageIdx),
                FullName = s.FullName,
                Mobile = s.Mobile,
                Email = s.Email,
                PreferredDateOrTripStart = s.Trip,
                NumberOfPeople = s.People,
                Message = s.Message,
                Status = s.Status,
                AssignedAgentId = s.AgentIdx.HasValue ? ServiceCatalogId("e6000000", s.AgentIdx.Value) : null,
                CreatedAt = s.History[0].CreatedAt,
                UpdatedAt = s.History[^1].CreatedAt,
            });

            db.InquiryStatusHistories.AddRange(s.History.Select(h => new InquiryStatusHistory
            {
                Id = Guid.NewGuid(),
                InquiryId = s.Id,
                Status = h.Status,
                ChangedByAdminId = h.AdminId,
                Note = h.Note,
                CreatedAt = h.CreatedAt,
            }));
        }
        await db.SaveChangesAsync();
    }
}
