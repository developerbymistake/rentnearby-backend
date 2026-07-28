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
        await SeedCreditFeaturesAsync(db);
        await SeedCreditPlansAsync(db);
        await SeedCreditPacksAsync(db);
        await SeedDistrictsAsync(db);
        await SeedCitiesAsync(db);
        await SeedListingLimitSettingsAsync(db);
        await SeedAppFeatureFlagsAsync(db);
        await SeedCouponsAsync(db);
        await SeedAdminsAsync(db);

        // Local Services Marketplace catalog — Categories are the top level (one consumer rail per
        // active category): Char Dham Yatra + Tour, Travel & Camping (Travel) and Yoga & Wellness
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
            (3, "Yoga & Wellness",        "activity",     ServiceCategoryFormTypes.Consultation, "Instructor"),
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
            (11, "Breakfast",        "coffee"),
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
        // Char Dham Yatra holds one Service per real, independently-bookable offering (each with its
        // own genuine ex-Haridwar itinerary and tiered per-group-size pricing in
        // SeedServicePackagesAsync below) — Do Dham and Char Dham are each their own Service, not a
        // package nested under a single "combo" Service, matching how every offering here is sold.
        var services = new (int Index, int CategoryIdx, string Name, string Icon, string Short, string Full, bool Featured)[]
        {
            // Char Dham Yatra (category 1) — 4 independent yatras, all ex-Haridwar
            (1, 1, "Badrinath Yatra", "route_square",
                "Ex-Haridwar pilgrimage to Badrinath, customized to your group.",
                "Begin your Badrinath Yatra from Haridwar, with stay, meals and cab arranged for the full journey. Halts, stay type and travel pace can be customized to match how your group wants to travel.",
                true),
            (2, 1, "Kedarnath Yatra", "route_square",
                "Ex-Haridwar pilgrimage to Kedarnath, customized to your group.",
                "Travel to Kedarnath Dham starting from Haridwar, with stay, meals and cab included throughout. The route and halts can be adjusted to suit your group's needs and preferences.",
                true),
            (3, 1, "Do Dham Yatra", "route_square",
                "Ex-Haridwar Do Dham Yatra — Kedarnath and Badrinath together.",
                "Cover both Kedarnath and Badrinath in one trip from Haridwar, with stay, meals and cab arranged throughout. The itinerary can be customized — halts, duration, stay type — based on what your group needs.",
                true),
            (4, 1, "Char Dham Yatra", "route_square",
                "Ex-Haridwar Char Dham Yatra — all four dhams, one journey.",
                "The complete Char Dham Yatra — Yamunotri, Gangotri, Kedarnath and Badrinath — starting from Haridwar, with stay, meals and cab arranged for the full journey. Duration and halts can be customized to how your group wants to travel.",
                true),

            // Tour, Travel & Camping (category 2) — 11 real, agent-provided itineraries, same
            // ex-Haridwar/Uttarakhand tour model as Char Dham Yatra: tiered per-group-size pricing,
            // duration fixed per Service, cab used in description text (not "transport").
            (5, 2, "Mussoorie & Dhanaulti Tour", "airplane",
                "2N/3D covering Mussoorie and Dhanaulti's top spots.",
                "A 2-night, 3-day tour covering Mussoorie and Dhanaulti, with hotel stay, cab and breakfast included, plus local sightseeing throughout.",
                true),
            (6, 2, "Nainital Tour", "airplane",
                "2N/3D covering Nainital's top sightseeing spots.",
                "A 2-night, 3-day tour of Nainital, with hotel stay, cab and breakfast included, plus local sightseeing throughout.",
                true),
            (7, 2, "Nainital & Corbett Tour", "airplane",
                "3N/4D covering Nainital and Jim Corbett.",
                "A 3-night, 4-day tour covering Nainital and Jim Corbett, with hotel stay, cab and breakfast included, plus local sightseeing throughout.",
                true),
            (8, 2, "Auli Tour", "airplane",
                "2N/3D covering Auli's top sightseeing spots.",
                "A 2-night, 3-day tour of Auli, with hotel stay, cab and breakfast included, plus local sightseeing throughout.",
                true),
            (9, 2, "Rishikesh & Haridwar Tour", "airplane",
                "2N/3D covering Rishikesh and Haridwar.",
                "A 2-night, 3-day tour covering Rishikesh and Haridwar, with hotel stay, cab and breakfast included, plus local sightseeing throughout.",
                true),
            (10, 2, "Chopta & Tungnath Tour", "airplane",
                "2N/3D covering Chopta and Tungnath.",
                "A 2-night, 3-day tour covering Chopta and Tungnath, with hotel stay, cab and breakfast included, plus local sightseeing throughout.",
                true),
            (11, 2, "Mukteshwar & Ranikhet Tour", "airplane",
                "2N/3D covering Mukteshwar and Ranikhet.",
                "A 2-night, 3-day tour covering Mukteshwar and Ranikhet, with hotel stay, cab and breakfast included, plus local sightseeing throughout.",
                true),
            (12, 2, "Kumaun Lakes, Orchards & Hills Tour", "airplane",
                "4N/5D covering Nainital, Kausani and Ranikhet.",
                "A 4-night, 5-day tour covering Nainital, Kausani and Ranikhet, with hotel stay, cab and breakfast included, plus local sightseeing throughout.",
                true),
            (13, 2, "Garhwal Triangle Tour", "airplane",
                "4N/5D covering Haridwar, Rishikesh and Mussoorie.",
                "A 4-night, 5-day tour covering Haridwar, Rishikesh and Mussoorie, with hotel stay, cab and breakfast included, plus local sightseeing throughout.",
                true),
            (14, 2, "Grand Kumaun Circuit", "airplane",
                "6N/7D covering Nainital, Kausani, Ranikhet and Jim Corbett.",
                "A 6-night, 7-day tour covering Nainital, Kausani, Ranikhet and Jim Corbett, with hotel stay, cab and breakfast included, plus local sightseeing throughout.",
                true),
            (15, 2, "Garhwal Adventure, Hills & Spirituality Tour", "airplane",
                "6N/7D covering Haridwar, Rishikesh, Mussoorie and Dhanaulti.",
                "A 6-night, 7-day tour covering Haridwar, Rishikesh, Mussoorie and Dhanaulti, with hotel stay, cab and breakfast included, plus local sightseeing throughout.",
                true),

            // Yoga & Wellness (category 3) — Consultation vertical: every plan is a custom quote,
            // the team hears the query and quotes offline (platform is the middleman only). Services
            // 18-23 are condition-specific yoga therapy groups, same instructor team as Yoga
            // Sessions/Diet Plan, each named condition below is that group's own Package.
            (16, 3, "Yoga Sessions", "activity",
                "Private one-on-one sessions or corporate workshops.",
                "Yoga sessions with an instructor — private one-on-one (regular or certified instructor) or a corporate workshop (single session or monthly program). Share your requirement and get a custom quote.",
                false),
            (17, 3, "Personalised Diet Plan", "weight",
                "Personalised diet plans from a certified nutritionist — weight loss, weight gain or diabetic-friendly.",
                "Personalised diet plans from a certified nutritionist, with ongoing consultation support. Choose a weight-loss, weight-gain or diabetic-friendly plan — share your requirement and get a custom quote.",
                true),
            (18, 3, "Hormones & Weight (Metabolic)", "activity",
                "Yoga-based support for diabetes, thyroid, PCOD/PCOS, obesity and prostate health.",
                "Yoga-based support for metabolic and hormonal conditions — diabetes, thyroid care, prostate health, PCOD/PCOS and obesity. Share your condition and get a custom quote.",
                false),
            (19, 3, "Heart & Blood Pressure", "activity",
                "Yoga-based support for hypertension and heart health.",
                "Yoga-based support for hypertension and overall heart health. Share your condition and get a custom quote.",
                false),
            (20, 3, "Mental Health & Nerves", "activity",
                "Yoga-based support for anxiety, depression, insomnia and Parkinson's.",
                "Yoga-based support for anxiety and chronic stress, depression, insomnia and sleep disorders, and Parkinson's. Share your condition and get a custom quote.",
                false),
            (21, 3, "Bones, Joints & Back Pain", "activity",
                "Yoga-based support for back pain, arthritis, spondylosis and sciatica.",
                "Yoga-based support for chronic back pain, arthritis, spondylosis (cervical and lumbar) and sciatica. Share your condition and get a custom quote.",
                false),
            (22, 3, "Breathing & Lungs", "activity",
                "Yoga-based support for asthma and chronic bronchitis.",
                "Yoga-based support for asthma and chronic bronchitis. Share your condition and get a custom quote.",
                false),
            (23, 3, "Stomach & Digestion", "activity",
                "Yoga-based support for indigestion, constipation, acid reflux and fatty liver.",
                "Yoga-based support for indigestion, constipation, acid reflux and fatty liver. Share your condition and get a custom quote.",
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
        // Price=null renders "Get Custom Quote" — EVERY Yoga & Wellness (Consultation) plan is null: the
        // agent hears the query and quotes offline, the platform never commits a price for that
        // vertical. IsStartingAtPrice is true on every priced (Travel) row — yatra/camping/tour
        // pricing is genuinely variable, so "Starting at ₹X" belongs wherever a real price exists.
        // Plan names are simple and concrete (by travel mode / duration / session type / minimum
        // group size), never abstract tier labels like "Standard/Deluxe/Premium". SortOrder is set
        // price-ascending within each Service (cheapest tier first), not creation order, so the
        // Package List screen (which orders strictly by SortOrder — see
        // ServicePackageRepository.GetByServiceIdAsync) always shows the lowest per-person rate at
        // the top rather than in whatever order group-size tiers happen to have been given.
        var packages = new (int Index, int ServiceIdx, string Name, int? Price, int? OriginalPrice, int? DiscountPercent,
            bool StartingAt, int? Days, int? Nights, string? Unit, int SortOrder, bool Featured)[]
        {
            // Badrinath Yatra (service 1) — real agent-provided pricing, tiered by minimum group
            // size (larger group = lower per-person rate); duration is identical across all 3 tiers.
            (1, 1, "Minimum 12 Persons", 6500, null, null, true, 3, 2, "per person", 1, false),
            (2, 1, "Minimum 4 Persons", 7500, null, null, true, 3, 2, "per person", 2, false),
            (3, 1, "Minimum 2 Persons", 10500, null, null, true, 3, 2, "per person", 3, false),

            // Kedarnath Yatra (service 2)
            (4, 2, "Minimum 12 Persons", 9000, null, null, true, 4, 3, "per person", 1, false),
            (5, 2, "Minimum 4 Persons", 10500, null, null, true, 4, 3, "per person", 2, false),
            (6, 2, "Minimum 2 Persons", 14500, null, null, true, 4, 3, "per person", 3, false),

            // Do Dham Yatra (service 3) — duration changed to 5N/6D
            (7, 3, "Minimum 12 Persons", 15000, null, null, true, 6, 5, "per person", 1, false),
            (8, 3, "Minimum 4 Persons", 16500, null, null, true, 6, 5, "per person", 2, false),
            (9, 3, "Minimum 2 Persons", 22500, null, null, true, 6, 5, "per person", 3, false),

            // Char Dham Yatra (service 4)
            (10, 4, "Minimum 12 Persons", 22500, null, null, true, 10, 9, "per person", 1, false),
            (11, 4, "Minimum 4 Persons", 25500, null, null, true, 10, 9, "per person", 2, false),
            (12, 4, "Minimum 2 Persons", 35500, null, null, true, 10, 9, "per person", 3, false),

            // Mussoorie & Dhanaulti Tour (service 5)
            (13, 5, "Minimum 12 Persons", 7000, null, null, true, 3, 2, "per person", 1, false),
            (14, 5, "Minimum 4 Persons", 7500, null, null, true, 3, 2, "per person", 2, false),
            (15, 5, "Minimum 2 Persons", 10500, null, null, true, 3, 2, "per person", 3, false),

            // Nainital Tour (service 6)
            (16, 6, "Minimum 12 Persons", 7000, null, null, true, 3, 2, "per person", 1, false),
            (17, 6, "Minimum 4 Persons", 7500, null, null, true, 3, 2, "per person", 2, false),
            (18, 6, "Minimum 2 Persons", 10500, null, null, true, 3, 2, "per person", 3, false),

            // Nainital & Corbett Tour (service 7)
            (19, 7, "Minimum 12 Persons", 8500, null, null, true, 4, 3, "per person", 1, false),
            (20, 7, "Minimum 4 Persons", 9000, null, null, true, 4, 3, "per person", 2, false),
            (21, 7, "Minimum 2 Persons", 13750, null, null, true, 4, 3, "per person", 3, false),

            // Auli Tour (service 8)
            (22, 8, "Minimum 12 Persons", 7000, null, null, true, 3, 2, "per person", 1, false),
            (23, 8, "Minimum 4 Persons", 7500, null, null, true, 3, 2, "per person", 2, false),
            (24, 8, "Minimum 2 Persons", 10500, null, null, true, 3, 2, "per person", 3, false),

            // Rishikesh & Haridwar Tour (service 9)
            (25, 9, "Minimum 12 Persons", 6500, null, null, true, 3, 2, "per person", 1, false),
            (26, 9, "Minimum 4 Persons", 7500, null, null, true, 3, 2, "per person", 2, false),
            (27, 9, "Minimum 2 Persons", 10000, null, null, true, 3, 2, "per person", 3, false),

            // Chopta & Tungnath Tour (service 10)
            (28, 10, "Minimum 12 Persons", 6750, null, null, true, 3, 2, "per person", 1, false),
            (29, 10, "Minimum 4 Persons", 7500, null, null, true, 3, 2, "per person", 2, false),
            (30, 10, "Minimum 2 Persons", 10500, null, null, true, 3, 2, "per person", 3, false),

            // Mukteshwar & Ranikhet Tour (service 11)
            (31, 11, "Minimum 12 Persons", 7000, null, null, true, 3, 2, "per person", 1, false),
            (32, 11, "Minimum 4 Persons", 7500, null, null, true, 3, 2, "per person", 2, false),
            (33, 11, "Minimum 2 Persons", 10500, null, null, true, 3, 2, "per person", 3, false),

            // Kumaun Lakes, Orchards & Hills Tour (service 12)
            (34, 12, "Minimum 12 Persons", 8500, null, null, true, 5, 4, "per person", 1, false),
            (35, 12, "Minimum 4 Persons", 12500, null, null, true, 5, 4, "per person", 2, false),
            (36, 12, "Minimum 2 Persons", 17500, null, null, true, 5, 4, "per person", 3, false),

            // Garhwal Triangle Tour (service 13)
            (37, 13, "Minimum 12 Persons", 10000, null, null, true, 5, 4, "per person", 1, false),
            (38, 13, "Minimum 4 Persons", 11500, null, null, true, 5, 4, "per person", 2, false),
            (39, 13, "Minimum 2 Persons", 16500, null, null, true, 5, 4, "per person", 3, false),

            // Grand Kumaun Circuit (service 14) — source pricing given as Min 4 (17,500) higher
            // than Min 2 (16,500); kept exactly as provided, SortOrder follows actual price
            // ascending (not the usual min-12/4/2 order) per this file's own SortOrder convention.
            (40, 14, "Minimum 12 Persons", 14750, null, null, true, 7, 6, "per person", 1, false),
            (41, 14, "Minimum 2 Persons", 16500, null, null, true, 7, 6, "per person", 2, false),
            (42, 14, "Minimum 4 Persons", 17500, null, null, true, 7, 6, "per person", 3, false),

            // Garhwal Adventure, Hills & Spirituality Tour (service 15) — same Min 4 > Min 2
            // source-data quirk as Grand Kumaun Circuit above, kept as provided.
            (43, 15, "Minimum 12 Persons", 14750, null, null, true, 7, 6, "per person", 1, false),
            (44, 15, "Minimum 2 Persons", 16500, null, null, true, 7, 6, "per person", 2, false),
            (45, 15, "Minimum 4 Persons", 17500, null, null, true, 7, 6, "per person", 3, false),

            // Yoga Sessions (service 16) — merged 1-on-1 + Corporate Workshop packages
            (46, 16, "Regular Session", null, null, null, false, null, null, null, 1, false),
            (47, 16, "Session with Certified Instructor", null, null, null, false, null, null, null, 2, true),
            (48, 16, "Single Session Workshop", null, null, null, false, null, null, null, 3, false),
            (49, 16, "Monthly Corporate Program", null, null, null, false, null, null, null, 4, true),

            // Personalised Diet Plan (service 17)
            (50, 17, "Weight Loss Plan", null, null, null, false, null, null, null, 1, true),
            (51, 17, "Weight Gain Plan", null, null, null, false, null, null, null, 2, false),
            (52, 17, "Diabetic-Friendly Plan", null, null, null, false, null, null, null, 3, false),

            // Hormones & Weight (Metabolic) (service 18)
            (53, 18, "Diabetes", null, null, null, false, null, null, null, 1, false),
            (54, 18, "Thyroid Care", null, null, null, false, null, null, null, 2, false),
            (55, 18, "Prostate Health", null, null, null, false, null, null, null, 3, false),
            (56, 18, "PCOD and PCOS", null, null, null, false, null, null, null, 4, false),
            (57, 18, "Obesity", null, null, null, false, null, null, null, 5, false),

            // Heart & Blood Pressure (service 19)
            (58, 19, "Hypertension", null, null, null, false, null, null, null, 1, false),
            (59, 19, "Heart Health", null, null, null, false, null, null, null, 2, false),

            // Mental Health & Nerves (service 20)
            (60, 20, "Anxiety & Chronic Stress", null, null, null, false, null, null, null, 1, false),
            (61, 20, "Depression", null, null, null, false, null, null, null, 2, false),
            (62, 20, "Insomnia & Sleep Disorders", null, null, null, false, null, null, null, 3, false),
            (63, 20, "Parkinson's", null, null, null, false, null, null, null, 4, false),

            // Bones, Joints & Back Pain (service 21)
            (64, 21, "Chronic Back Pain", null, null, null, false, null, null, null, 1, false),
            (65, 21, "Arthritis", null, null, null, false, null, null, null, 2, false),
            (66, 21, "Spondylosis (Cervical and Lumbar)", null, null, null, false, null, null, null, 3, false),
            (67, 21, "Sciatica", null, null, null, false, null, null, null, 4, false),

            // Breathing & Lungs (service 22)
            (68, 22, "Asthma", null, null, null, false, null, null, null, 1, false),
            (69, 22, "Chronic Bronchitis", null, null, null, false, null, null, null, 2, false),

            // Stomach & Digestion (service 23)
            (70, 23, "Indigestion", null, null, null, false, null, null, null, 1, false),
            (71, 23, "Constipation", null, null, null, false, null, null, null, 2, false),
            (72, 23, "Acid Reflux", null, null, null, false, null, null, null, 3, false),
            (73, 23, "Fatty Liver", null, null, null, false, null, null, null, 4, false),
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

        // (packageIndex, inclusionIndices[]) — only Tourism packages get inclusions wired up
        // (Consultation packages have no physical "inclusions" concept). Indices here match the
        // restructured package list in SeedServicePackagesAsync above.
        var mappings = new (int PackageIdx, int[] InclusionIdxs)[]
        {
            // All 4 Char Dham Yatra services (packages 1-12) — every group-size tier of every yatra
            // includes the same 3: Hotel Stay, Meals, Local Transport. Group size changes the price,
            // never what's included.
            (1, new[] { 1, 2, 3 }), (2, new[] { 1, 2, 3 }), (3, new[] { 1, 2, 3 }),
            (4, new[] { 1, 2, 3 }), (5, new[] { 1, 2, 3 }), (6, new[] { 1, 2, 3 }),
            (7, new[] { 1, 2, 3 }), (8, new[] { 1, 2, 3 }), (9, new[] { 1, 2, 3 }),
            (10, new[] { 1, 2, 3 }), (11, new[] { 1, 2, 3 }), (12, new[] { 1, 2, 3 }),

            // 11 new Tour, Travel & Camping services (packages 13-45) — every group-size tier of
            // every tour includes the same 4: Hotel Stay, Local Transport, Sightseeing, Breakfast.
            (13, new[] { 1, 3, 6, 11 }), (14, new[] { 1, 3, 6, 11 }), (15, new[] { 1, 3, 6, 11 }),
            (16, new[] { 1, 3, 6, 11 }), (17, new[] { 1, 3, 6, 11 }), (18, new[] { 1, 3, 6, 11 }),
            (19, new[] { 1, 3, 6, 11 }), (20, new[] { 1, 3, 6, 11 }), (21, new[] { 1, 3, 6, 11 }),
            (22, new[] { 1, 3, 6, 11 }), (23, new[] { 1, 3, 6, 11 }), (24, new[] { 1, 3, 6, 11 }),
            (25, new[] { 1, 3, 6, 11 }), (26, new[] { 1, 3, 6, 11 }), (27, new[] { 1, 3, 6, 11 }),
            (28, new[] { 1, 3, 6, 11 }), (29, new[] { 1, 3, 6, 11 }), (30, new[] { 1, 3, 6, 11 }),
            (31, new[] { 1, 3, 6, 11 }), (32, new[] { 1, 3, 6, 11 }), (33, new[] { 1, 3, 6, 11 }),
            (34, new[] { 1, 3, 6, 11 }), (35, new[] { 1, 3, 6, 11 }), (36, new[] { 1, 3, 6, 11 }),
            (37, new[] { 1, 3, 6, 11 }), (38, new[] { 1, 3, 6, 11 }), (39, new[] { 1, 3, 6, 11 }),
            (40, new[] { 1, 3, 6, 11 }), (41, new[] { 1, 3, 6, 11 }), (42, new[] { 1, 3, 6, 11 }),
            (43, new[] { 1, 3, 6, 11 }), (44, new[] { 1, 3, 6, 11 }), (45, new[] { 1, 3, 6, 11 }),
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
