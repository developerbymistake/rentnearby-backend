using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using RentNearBy.Core.Entities;
using System.Text.Json;

namespace RentNearBy.Infrastructure.Data;

public static class DataSeeder
{
    public static async Task SeedAsync(ApplicationDbContext db)
    {
        await SeedRoomTypesAsync(db);
        await SeedPlotTypesAsync(db);
        await SeedPlansAsync(db);
        await SeedPlotPlansAsync(db);
        await SeedDistrictsFromGadmAsync(db);
        await SeedFeaturesAsync(db);
        await SeedAdminsAsync(db);
    }

    private static async Task SeedPlansAsync(ApplicationDbContext db)
    {
        if (await db.Plans.AnyAsync()) return;

        var plans = new[]
        {
            new Plan { Id = Guid.NewGuid(), PlanType = "FREE", Days = 2, RoomLimit = 1, Price = 0, IsEnabled = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new Plan { Id = Guid.NewGuid(), PlanType = "PAID", Days = 30, RoomLimit = 2, Price = 99, IsEnabled = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        };

        db.Plans.AddRange(plans);
        await db.SaveChangesAsync();
    }

    private static async Task SeedPlotPlansAsync(ApplicationDbContext db)
    {
        if (await db.PlotPlans.AnyAsync()) return;

        var plans = new[]
        {
            new PlotPlan { Id = Guid.NewGuid(), PlanType = "FREE", Days = 2, PlotLimit = 1, Price = 0, IsEnabled = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new PlotPlan { Id = Guid.NewGuid(), PlanType = "PAID", Days = 30, PlotLimit = 2, Price = 99, IsEnabled = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        };

        db.PlotPlans.AddRange(plans);
        await db.SaveChangesAsync();
    }

    private static async Task SeedRoomTypesAsync(ApplicationDbContext db)
    {
        if (await db.RoomTypes.AnyAsync()) return;

        var roomTypes = new[]
        {
            new RoomType { Id = Guid.NewGuid(), Name = "1BHK",   SortOrder = 1, Description = "1 bedroom, hall and kitchen",           CreatedAt = DateTime.UtcNow },
            new RoomType { Id = Guid.NewGuid(), Name = "2BHK",   SortOrder = 2, Description = "2 bedroom, hall and kitchen",           CreatedAt = DateTime.UtcNow },
            new RoomType { Id = Guid.NewGuid(), Name = "3BHK",   SortOrder = 3, Description = "3 bedroom, hall and kitchen",           CreatedAt = DateTime.UtcNow },
            new RoomType { Id = Guid.NewGuid(), Name = "1RK",    SortOrder = 4, Description = "Single room with kitchen",              CreatedAt = DateTime.UtcNow },
            new RoomType { Id = Guid.NewGuid(), Name = "PG",     SortOrder = 5, Description = "Paying guest accommodation",            CreatedAt = DateTime.UtcNow },
            new RoomType { Id = Guid.NewGuid(), Name = "Hostel", SortOrder = 6, Description = "Shared dormitory-style accommodation",  CreatedAt = DateTime.UtcNow },
        };

        db.RoomTypes.AddRange(roomTypes);
        await db.SaveChangesAsync();
    }

    private static async Task SeedPlotTypesAsync(ApplicationDbContext db)
    {
        if (await db.PlotTypes.AnyAsync()) return;

        var plotTypes = new[]
        {
            new PlotType { Id = Guid.Parse("b1000000-0000-0000-0000-000000000001"), Name = "Residential",  SortOrder = 1, Description = "Residential land for housing",         CreatedAt = DateTime.UtcNow },
            new PlotType { Id = Guid.Parse("b1000000-0000-0000-0000-000000000002"), Name = "Commercial",   SortOrder = 2, Description = "Commercial land for business use",      CreatedAt = DateTime.UtcNow },
            new PlotType { Id = Guid.Parse("b1000000-0000-0000-0000-000000000003"), Name = "Agricultural", SortOrder = 3, Description = "Agricultural land for farming use",     CreatedAt = DateTime.UtcNow },
        };

        db.PlotTypes.AddRange(plotTypes);
        await db.SaveChangesAsync();
    }

    private static readonly GeometryFactory GeoFactory = new(new PrecisionModel(), 4326);

    private static async Task SeedDistrictsFromGadmAsync(ApplicationDbContext db)
    {
        if (await db.Districts.AnyAsync()) return;

        var asm = typeof(DataSeeder).Assembly;
        using var stream = asm.GetManifestResourceStream("RentNearBy.Infrastructure.Data.gadm41_IND_2.json");
        if (stream == null)
        {
            Console.WriteLine("[DataSeeder] GADM JSON resource not found — skipping district seeding.");
            return;
        }

        using var reader = new StreamReader(stream);
        var json = await reader.ReadToEndAsync();
        using var doc = JsonDocument.Parse(json);

        var features = doc.RootElement.GetProperty("features").EnumerateArray().ToList();
        Console.WriteLine($"[DataSeeder] Seeding {features.Count} districts from GADM...");

        var batch = new List<District>(50);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seeded = 0;

        foreach (var feature in features)
        {
            var props = feature.GetProperty("properties");
            var name = props.GetProperty("NAME_2").GetString()?.Trim() ?? "";
            var stateName = props.GetProperty("NAME_1").GetString()?.Trim() ?? "";
            if (string.IsNullOrEmpty(name)) continue;
            if (!seen.Add($"{stateName}|{name}")) continue; // skip GADM duplicates

            var boundary = ParseGeometry(feature.GetProperty("geometry"));

            batch.Add(new District
            {
                Id = Guid.NewGuid(),
                Name = name,
                StateName = stateName,
                IsActive = false,
                Boundary = boundary,
                CreatedAt = DateTime.UtcNow,
            });

            if (batch.Count >= 50)
            {
                db.Districts.AddRange(batch);
                await db.SaveChangesAsync();
                seeded += batch.Count;
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            db.Districts.AddRange(batch);
            await db.SaveChangesAsync();
            seeded += batch.Count;
        }

        Console.WriteLine($"[DataSeeder] District seeding complete. Total: {seeded}");
    }

    private static Geometry? ParseGeometry(JsonElement geom)
    {
        var type = geom.GetProperty("type").GetString();
        var coords = geom.GetProperty("coordinates");
        return type switch
        {
            "Polygon" => ParsePolygon(coords),
            "MultiPolygon" => ParseMultiPolygon(coords),
            _ => null,
        };
    }

    private static Polygon? ParsePolygon(JsonElement coords)
    {
        var rings = coords.EnumerateArray().ToArray();
        if (rings.Length == 0) return null;
        var shell = ParseRing(rings[0]);
        if (shell == null) return null;
        var holes = rings.Skip(1).Select(ParseRing).Where(r => r != null).Cast<LinearRing>().ToArray();
        return GeoFactory.CreatePolygon(shell, holes);
    }

    private static MultiPolygon ParseMultiPolygon(JsonElement coords)
    {
        var polygons = coords.EnumerateArray()
            .Select(ParsePolygon).Where(p => p != null).Cast<Polygon>().ToArray();
        return GeoFactory.CreateMultiPolygon(polygons);
    }

    private static LinearRing? ParseRing(JsonElement ring)
    {
        var pts = ring.EnumerateArray().Select(c =>
        {
            var arr = c.EnumerateArray().ToArray();
            return new Coordinate(arr[0].GetDouble(), arr[1].GetDouble());
        }).ToArray();
        return pts.Length >= 4 ? GeoFactory.CreateLinearRing(pts) : null;
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
                IsEnabled = false,
                FreeLimit = 1,
                FreeDays = 30,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            },
            new AppFeature
            {
                Id = Guid.NewGuid(),
                Key = "plot_payment",
                DisplayName = "Plot Payment",
                IsEnabled = false,
                FreeLimit = 1,
                FreeDays = 30,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            }
        );
        await db.SaveChangesAsync();
    }

    private static async Task SeedAdminsAsync(ApplicationDbContext db)
    {
        var adminPhones = new[] { "7060023511", "9720565640" };
        bool changed = false;

        foreach (var phone in adminPhones)
        {
            var exists = await db.Admins.AnyAsync(a => a.PhoneNumber == phone);
            if (!exists)
            {
                db.Admins.Add(new Admin
                {
                    Id = Guid.NewGuid(),
                    PhoneNumber = phone,
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
