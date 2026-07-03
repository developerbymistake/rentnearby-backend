using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using RentNearBy.Core.Entities;
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
        try
        {
            await SeedRoomTypesAsync(db);
        }
        catch (Exception ex)
        {

            Console.WriteLine($"[SeedRoomTypesAsync] Seeding {ex.Message} RoomType...");
        }
        //await SeedPlotTypesAsync(db);
        //await SeedPlansAsync(db);
        //await SeedPlotPlansAsync(db);
        //await SeedDistrictsAsync(db);
        //await SeedCitiesAsync(db);
        //await SeedFeaturesAsync(db);
        //await SeedAdminsAsync(db);
    }

    private static async Task SeedPlansAsync(ApplicationDbContext db)
    {
        if (await db.RoomPlans.AnyAsync()) return;

        var plans = new[]
        {
            new RoomPlan { Id = Guid.NewGuid(), PlanType = "BASIC",    Days = 5,  RoomLimit = 1, Price = 99,  DiscountPercent = 100, OriginalPrice = 0,  IsEnabled = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new RoomPlan { Id = Guid.NewGuid(), PlanType = "STANDARD", Days = 30, RoomLimit = 2, Price = 199, DiscountPercent = 50,  OriginalPrice = 99, IsEnabled = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        };

        db.RoomPlans.AddRange(plans);
        await db.SaveChangesAsync();
    }

    private static async Task SeedPlotPlansAsync(ApplicationDbContext db)
    {
        if (await db.PlotPlans.AnyAsync()) return;

        var plans = new[]
        {
            new PlotPlan { Id = Guid.NewGuid(), PlanType = "BASIC",    Days = 5,  PlotListingLimit = 1, Price = 99,  DiscountPercent = 100, OriginalPrice = 0,  IsEnabled = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new PlotPlan { Id = Guid.NewGuid(), PlanType = "STANDARD", Days = 30, PlotListingLimit = 2, Price = 199, DiscountPercent = 50,  OriginalPrice = 99, IsEnabled = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        };

        db.PlotPlans.AddRange(plans);
        await db.SaveChangesAsync();
    }

    private static async Task SeedRoomTypesAsync(ApplicationDbContext db)
    {
        Console.WriteLine("[RoomTypes] Enter start.");
        if (await db.RoomTypes.AnyAsync()) return;

        Console.WriteLine("[RoomTypes] Enter Inner.");
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
        Console.WriteLine("[RoomTypes] Enter End.");
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
        };

        db.PlotTypes.AddRange(plotTypes);
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

    private static async Task SeedFeaturesAsync(ApplicationDbContext db)
    {
        if (await db.AppFeatures.AnyAsync()) return;

        db.AppFeatures.AddRange(
            new AppFeature
            {
                Id = Guid.NewGuid(),
                Key = "room_payment",
                DisplayName = "Room Payment",
                IsEnabled = true,
                FreeLimit = 1,
                FreeDays = 5,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            },
            new AppFeature
            {
                Id = Guid.NewGuid(),
                Key = "plot_payment",
                DisplayName = "PlotListing Payment",
                IsEnabled = true,
                FreeLimit = 1,
                FreeDays = 5,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            }
        );
        await db.SaveChangesAsync();
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
