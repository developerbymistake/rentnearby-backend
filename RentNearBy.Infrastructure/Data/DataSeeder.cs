using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Models;
using RentNearBy.Core.Utils;
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
        await SeedCreditFeaturesAsync(db);
        await SeedCreditPlansAsync(db);
        await SeedCreditPacksAsync(db);
        await SeedDistrictsAsync(db);
        await SeedCitiesAsync(db);
        await SeedListingLimitSettingsAsync(db);
        await SeedAppFeatureFlagsAsync(db);
        await SeedAppTabsAsync(db);
        await SeedCouponsAsync(db);
        await SeedAdminsAsync(db);
    }

    private static async Task SeedCouponsAsync(ApplicationDbContext db)
    {
        if (await db.Coupons.AnyAsync(c => c.Id == WellKnownCoupons.WelcomeSignupCouponId)) return;

        db.Coupons.Add(new Coupon
        {
            Id = WellKnownCoupons.WelcomeSignupCouponId,
            Code = null,
            CreditValue = 300,
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
            new ListingLimitSetting { Id = Guid.NewGuid(), ListingKind = ListingKinds.Room, MaxListings = 2, UpdatedAt = DateTime.UtcNow },
            new ListingLimitSetting { Id = Guid.NewGuid(), ListingKind = ListingKinds.Plot, MaxListings = 2, UpdatedAt = DateTime.UtcNow }
        );
        await db.SaveChangesAsync();
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
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    // Master table for the consumer app's 4 bottom-nav tabs — all start active with today's real
    // labels/order so behavior is identical to the pre-AppTab hardcoded AppTabs class in the mobile
    // app. Home/Profile are seeded active and stay that way forever (AdminHandlers.UpdateAppTab
    // refuses to deactivate them, see AppTabKeys.NonDeactivatable).
    private static async Task SeedAppTabsAsync(ApplicationDbContext db)
    {
        if (await db.AppTabs.AnyAsync()) return;

        var tabs = new (string Key, string DisplayName, int SortOrder)[]
        {
            (AppTabKeys.Home, "Home", 0),
            (AppTabKeys.Rooms, "Rooms", 1),
            (AppTabKeys.Plots, "Plots", 2),
            (AppTabKeys.Profile, "Profile", 3),
        };

        foreach (var (key, displayName, sortOrder) in tabs)
        {
            db.AppTabs.Add(new AppTab
            {
                Id = Guid.NewGuid(),
                TabKey = key,
                DisplayName = displayName,
                IsActive = true,
                SortOrder = sortOrder,
                UpdatedAt = DateTime.UtcNow,
            });
        }
        await db.SaveChangesAsync();
    }

    // Seed-only catalog of "what credits can be spent on" — Room/Plot Go-Live today, future credit-gated
    // features (contact reveal, chat, etc.) later, each as a new row here, never a schema change.
    private static async Task SeedCreditFeaturesAsync(ApplicationDbContext db)
    {
        if (await db.CreditFeatures.AnyAsync()) return;

        db.CreditFeatures.AddRange(
            new CreditFeature { Id = Guid.NewGuid(), Key = CreditFeatureKeys.RoomGoLive, DisplayName = "Room Go-Live", QuotaUnitLabel = "rooms", IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new CreditFeature { Id = Guid.NewGuid(), Key = CreditFeatureKeys.PlotGoLive, DisplayName = "Plot Go-Live", QuotaUnitLabel = "plots", IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow }
        );
        await db.SaveChangesAsync();
    }

    // Real credit-priced tiers matching the approved credit-economy mockups exactly (Basic/Standard/Premium,
    // Standard marked featured/"popular") — replaces the old RoomPlan/PlotPlan seed, which left stale
    // rupee-shaped placeholder values (Days=5/30, Price=99/199) behind after the credit-economy rework and
    // never seeded a third/Premium tier at all. Room and Plot use identical numbers, matching this
    // seeder's own prior precedent of equal values across both kinds.
    private static async Task SeedCreditPlansAsync(ApplicationDbContext db)
    {
        if (await db.CreditPlans.AnyAsync()) return;

        CreditPlan Make(string featureKey, string type, int days, int quota, int credits, bool featured) => new()
        {
            Id = Guid.NewGuid(),
            FeatureKey = featureKey,
            PlanType = type,
            Days = days,
            Quota = quota,
            Price = credits,
            DiscountPercent = 0,
            OriginalPrice = credits,
            IsFeatured = featured,
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        db.CreditPlans.AddRange(
            Make(CreditFeatureKeys.RoomGoLive, CreditPlanTypes.Basic, days: 15, quota: 1, credits: 99, featured: false),
            Make(CreditFeatureKeys.RoomGoLive, CreditPlanTypes.Standard, days: 30, quota: 2, credits: 299, featured: true),
            Make(CreditFeatureKeys.RoomGoLive, CreditPlanTypes.Premium, days: 60, quota: 3, credits: 499, featured: false),
            Make(CreditFeatureKeys.PlotGoLive, CreditPlanTypes.Basic, days: 15, quota: 1, credits: 99, featured: false),
            Make(CreditFeatureKeys.PlotGoLive, CreditPlanTypes.Standard, days: 30, quota: 2, credits: 299, featured: true),
            Make(CreditFeatureKeys.PlotGoLive, CreditPlanTypes.Premium, days: 60, quota: 3, credits: 499, featured: false)
        );
        await db.SaveChangesAsync();
    }

    // Credit packs (buy-credits tiers) — previously never seeded anywhere; the only INSERT path was the
    // admin app's create form, so GET /credit-packs/ had only ever returned an empty array on every real
    // deployment. Matches the approved mockups' Starter/Popular/Mega numbers exactly.
    private static async Task SeedCreditPacksAsync(ApplicationDbContext db)
    {
        if (await db.CreditPacks.AnyAsync()) return;

        CreditPack Make(int credits, int bonus, int priceInr, int sortOrder, bool featured) => new()
        {
            Id = Guid.NewGuid(),
            Credits = credits,
            BonusCredits = bonus,
            PriceInr = priceInr,
            IsEnabled = true,
            SortOrder = sortOrder,
            IsFeatured = featured,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        db.CreditPacks.AddRange(
            Make(credits: 100, bonus: 0, priceInr: 99, sortOrder: 1, featured: false), // Starter
            Make(credits: 300, bonus: 30, priceInr: 299, sortOrder: 2, featured: true),  // Popular / Best Value
            Make(credits: 500, bonus: 50, priceInr: 399, sortOrder: 3, featured: false)  // Mega
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
}
