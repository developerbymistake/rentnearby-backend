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

        // Local Services Marketplace catalog — Categories are the top level (one consumer rail per
        // active category): Char Dham Yatra + Tour, Travel & Camping (Travel) and Yoga & Diet
        // (Consultation), on the Category->Service->Package engine. Order matters: each method below
        // FK-references rows created by an earlier one via the deterministic ServiceCatalogId() ids,
        // not a DB round-trip.
        await SeedServiceCategoriesAsync(db);
        await SeedInclusionsAsync(db);
        await SeedServicesAsync(db);
        await SeedServicePackagesAsync(db);
        await SeedPackageInclusionsAsync(db);
        // No Agent/sample-Inquiry seeding — an Agent is now a role linked to a real User account
        // (Agent.UserId), so there's nothing meaningful to fabricate here; Admin links real Agents
        // to real accounts through the admin panel.
    }

    // Deterministic per-entity-type GUID, matching SeedQuestionTemplatesAsync's/SeedPlotTypesAsync's
    // Guid.Parse("<prefix>-...") style — lets a later seed method in this file (Packages -> Services,
    // Inquiries -> Packages/Agents) reference an earlier row's Id without a DB round-trip.
    // Prefixes used: e2=ServiceCategory, e3=Service, e4=ServicePackage, e5=Inclusion, e6=Agent,
    // e7=test consumer User, e8=Inquiry. (e1 belonged to the retired ServiceSection layer — do not
    // reuse it for a new entity type.)
    private static Guid ServiceCatalogId(string prefix, int n) => Guid.Parse($"{prefix}-0000-0000-0000-{n:D12}");

    private static async Task SeedCouponsAsync(ApplicationDbContext db)
    {
        if (await db.Coupons.AnyAsync(c => c.Id == WellKnownCoupons.WelcomeSignupCouponId)) return;

        db.Coupons.Add(new Coupon
        {
            Id = WellKnownCoupons.WelcomeSignupCouponId,
            Code = null,
            CoinValue = 300,
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
            Id = Guid.NewGuid(),
            FeatureKey = featureKey,
            PlanType = type,
            Days = days,
            Quota = quota,
            Price = coins,
            DiscountPercent = 0,
            OriginalPrice = coins,
            IsFeatured = featured,
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        db.CoinPlans.AddRange(
            Make(CoinFeatureKeys.RoomGoLive, CoinPlanTypes.Basic, days: 15, quota: 1, coins: 99, featured: false),
            Make(CoinFeatureKeys.RoomGoLive, CoinPlanTypes.Standard, days: 30, quota: 2, coins: 299, featured: true),
            Make(CoinFeatureKeys.RoomGoLive, CoinPlanTypes.Premium, days: 60, quota: 3, coins: 499, featured: false),
            Make(CoinFeatureKeys.PlotGoLive, CoinPlanTypes.Basic, days: 15, quota: 1, coins: 99, featured: false),
            Make(CoinFeatureKeys.PlotGoLive, CoinPlanTypes.Standard, days: 30, quota: 2, coins: 299, featured: true),
            Make(CoinFeatureKeys.PlotGoLive, CoinPlanTypes.Premium, days: 60, quota: 3, coins: 499, featured: false)
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
            Id = Guid.NewGuid(),
            Coins = coins,
            BonusCoins = bonus,
            PriceInr = priceInr,
            IsEnabled = true,
            SortOrder = sortOrder,
            IsFeatured = featured,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        db.CoinPacks.AddRange(
            Make(coins: 100, bonus: 0, priceInr: 99, sortOrder: 1, featured: false), // Starter
            Make(coins: 300, bonus: 30, priceInr: 299, sortOrder: 2, featured: true),  // Popular / Best Value
            Make(coins: 500, bonus: 50, priceInr: 399, sortOrder: 3, featured: false)  // Mega
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

    // ── Local Services Marketplace ─────────────────────────────────────────────────────────────────

    private static async Task SeedServiceCategoriesAsync(ApplicationDbContext db)
    {
        if (await db.ServiceCategories.AnyAsync()) return;

        // (index, name, icon, formType, agentRoleLabel) — Categories are the catalog's top level: one
        // consumer rail per active row, color-zoned client-side by rotation over this SortOrder (never
        // by name). Indices are contiguous — the whole catalog was renumbered when the ServiceSection
        // layer was retired and prod content was reset (RemoveServiceSectionsAndResetCatalog migration).
        var categories = new (int Index, string Name, string Icon, string FormType, string AgentRoleLabel)[]
        {
            (1, "Char Dham Yatra",        "route_square", ServiceCategoryFormTypes.Travel,       "Travel Expert"),
            (2, "Tour & Travel", "airplane",     ServiceCategoryFormTypes.Travel,       "Travel Expert"),
            (3, "Yoga & Diet",            "activity",     ServiceCategoryFormTypes.Consultation, "Instructor"),
        };

        var now = DateTime.UtcNow;
        db.ServiceCategories.AddRange(categories.Select(c => new ServiceCategory
        {
            Id = ServiceCatalogId("e2000000", c.Index),
            Name = c.Name,
            IconName = c.Icon,
            FormType = c.FormType,
            AgentRoleLabel = c.AgentRoleLabel,
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
        // dhams, different tour itineraries) are split into one Service per real offering here, each
        // getting its own genuine price/duration plans in SeedServicePackagesAsync below.
        var services = new (int Index, int CategoryIdx, string Name, string Icon, string Short, string Full, bool Featured)[]
        {
            // Char Dham Yatra (category 1) — 2 individual dhams + 1 all-4 combo
            (1, 1, "Badrinath Yatra", "route_square",
                "Guided pilgrimage to Badrinath by road.",
                "Pilgrimage packages to Badrinath Dham — travel by road with an experienced guide. Hotel stay and meals included.",
                true),
            (2, 1, "Kedarnath Yatra", "route_square",
                "Guided pilgrimage to Kedarnath by road/trek.",
                "Pilgrimage packages to Kedarnath Dham — trek/road journey with an experienced guide. Hotel stay and meals included.",
                true),
            (3, 1, "Char Dham Yatra (All 4 Combo)", "route_square",
                "Combined pilgrimage covering all 4 dhams — Kedarnath, Badrinath, Gangotri and Yamunotri.",
                "Complete Char Dham Yatra packages covering all four dhams together, with hotel stays, meals, local transport and an experienced tour guide. Choose from Do Dham (2 dhams) or full Char Dham (all 4).",
                true),

            // Tour, Travel & Camping (category 2) — one Service per itinerary/experience
            (4, 2, "Nainital-Mussoorie Duo Tour", "airplane",
                "5D/4N covering both Nainital and Mussoorie.",
                "A 5-day, 4-night tour covering both Nainital and Mussoorie's top sightseeing spots, with hotel stays and local transport.",
                true),
            (5, 2, "Riverside Camping", "tree",
                "Riverside camping with bonfire, for couples, friends or families.",
                "Riverside camping packages with bonfire and overnight stay — available as a standard 2D/1N trip or a family camping weekend.",
                true),

            // Yoga & Diet (category 3) — Consultation vertical: every plan is a custom quote, the
            // team hears the query and quotes offline (platform is the middleman only).
            (6, 3, "1-on-1 Yoga Session", "activity",
                "Private one-on-one yoga sessions.",
                "Private yoga sessions with an instructor, one-on-one — choose a regular session or one with a certified instructor. Share your requirement and get a custom quote.",
                false),
            (7, 3, "Corporate Yoga Workshop", "activity",
                "Yoga workshops for corporate teams.",
                "Yoga workshops for corporate teams — a single session or an ongoing monthly program. Share your requirement and get a custom quote.",
                false),
            (8, 3, "Personalised Diet Plan", "weight",
                "Personalised diet plans from a certified nutritionist — weight loss, weight gain or diabetic-friendly.",
                "Personalised diet plans from a certified nutritionist, with ongoing consultation support. Choose a weight-loss, weight-gain or diabetic-friendly plan — share your requirement and get a custom quote.",
                true),
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
        // Price=null renders "Get Custom Quote" — EVERY Yoga & Diet (Consultation) plan is null: the
        // agent hears the query and quotes offline, the platform never commits a price for that
        // vertical. IsStartingAtPrice is true on every priced (Travel) row — yatra/camping/tour
        // pricing is genuinely variable, so "Starting at ₹X" belongs wherever a real price exists.
        // Plan names are simple and concrete (by travel mode / duration / session type), never abstract
        // tier labels like "Standard/Deluxe/Premium".
        var packages = new (int Index, int ServiceIdx, string Name, int? Price, int? OriginalPrice, int? DiscountPercent,
            bool StartingAt, int? Days, int? Nights, string? Unit, int SortOrder, bool Featured)[]
        {
            // Badrinath Yatra (service 1) — helicopter package removed, road only
            (1, 1, "Badrinath Yatra by Road - 3D/2N", 7499, null, null, true, 3, 2, "per person", 1, true),

            // Kedarnath Yatra (service 2) — helicopter package removed, road/trek only
            (2, 2, "Kedarnath Yatra by Road/Trek - 4D/3N", 8999, null, null, true, 4, 3, "per person", 1, true),

            // Char Dham Yatra (All 4 Combo) (service 3) — all-helicopter combo removed
            (3, 3, "Do Dham Yatra (Kedarnath-Badrinath) - 6D/5N", 14999, 17999, 17, true, 6, 5, "per person", 1, true),
            (4, 3, "Char Dham Yatra Complete - 11D/10N", 27999, 32999, 15, true, 11, 10, "per person", 2, false),

            // Nainital-Mussoorie Duo Tour (service 4)
            (5, 4, "Nainital-Mussoorie Duo Tour", 8999, null, null, true, 5, 4, "per person", 1, true),

            // Riverside Camping (service 5)
            (6, 5, "Riverside Camping - 2D/1N", 2999, 3499, 14, true, 2, 1, "per person", 1, true),
            (7, 5, "Family Camping Weekend - 2D/1N", 3499, null, null, true, 2, 1, "per person", 2, false),

            // 1-on-1 Yoga Session (service 6)
            (8, 6, "Regular Session", null, null, null, false, null, null, null, 1, false),
            (9, 6, "Session with Certified Instructor", null, null, null, false, null, null, null, 2, true),

            // Corporate Yoga Workshop (service 7)
            (10, 7, "Single Session Workshop", null, null, null, false, null, null, null, 1, false),
            (11, 7, "Monthly Corporate Program", null, null, null, false, null, null, null, 2, true),

            // Personalised Diet Plan (service 8)
            (12, 8, "Weight Loss Plan", null, null, null, false, null, null, null, 1, true),
            (13, 8, "Weight Gain Plan", null, null, null, false, null, null, null, 2, false),
            (14, 8, "Diabetic-Friendly Plan", null, null, null, false, null, null, null, 3, false),
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
            (3, new[] { 1, 2, 3, 5 }),          // Do Dham Yatra: Hotel, Meals, Transport, Travel Insurance
            (4, new[] { 1, 2, 3, 4, 5 }),       // Char Dham Complete: + Tour Guide
            (5, new[] { 1, 2, 3, 6 }),          // Nainital-Mussoorie Duo Tour: + Sightseeing
            (6, new[] { 2, 4, 9 }),             // Riverside Camping: Meals, Tour Guide, First Aid Kit
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
}
