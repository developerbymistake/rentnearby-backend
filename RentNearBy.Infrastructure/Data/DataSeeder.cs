using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;

namespace RentNearBy.Infrastructure.Data;

public static class DataSeeder
{
    public static async Task SeedAsync(ApplicationDbContext db)
    {
        await SeedRoomTypesAsync(db);
        await SeedCitiesAsync(db);
    }

    private static async Task SeedRoomTypesAsync(ApplicationDbContext db)
    {
        if (await db.RoomTypes.AnyAsync()) return;

        var roomTypes = new[]
        {
            new RoomType { Id = Guid.NewGuid(), Name = "Single Room",   Description = "One unfurnished/semi-furnished room",      CreatedAt = DateTime.UtcNow },
            new RoomType { Id = Guid.NewGuid(), Name = "1 BHK",         Description = "1 bedroom, hall and kitchen apartment",    CreatedAt = DateTime.UtcNow },
            new RoomType { Id = Guid.NewGuid(), Name = "2 BHK",         Description = "2 bedroom, hall and kitchen apartment",    CreatedAt = DateTime.UtcNow },
            new RoomType { Id = Guid.NewGuid(), Name = "3 BHK",         Description = "3 bedroom, hall and kitchen apartment",    CreatedAt = DateTime.UtcNow },
            new RoomType { Id = Guid.NewGuid(), Name = "PG",            Description = "Paying guest accommodation",              CreatedAt = DateTime.UtcNow },
            new RoomType { Id = Guid.NewGuid(), Name = "Studio",        Description = "Compact single-room apartment with kitchenette", CreatedAt = DateTime.UtcNow },
            new RoomType { Id = Guid.NewGuid(), Name = "Hostel",        Description = "Shared dormitory-style accommodation",    CreatedAt = DateTime.UtcNow },
            new RoomType { Id = Guid.NewGuid(), Name = "Shared Room",   Description = "Room shared with one or more people",     CreatedAt = DateTime.UtcNow },
        };

        db.RoomTypes.AddRange(roomTypes);
        await db.SaveChangesAsync();
    }

    private static async Task SeedCitiesAsync(ApplicationDbContext db)
    {
        if (await db.Cities.AnyAsync()) return;

        var cities = new[]
        {
            new { Name = "Mumbai",     Lat = 19.0760m,  Lng = 72.8777m,  Districts = new[] { "Andheri", "Bandra", "Borivali", "Dadar", "Kurla", "Thane", "Navi Mumbai", "Powai" } },
            new { Name = "Delhi",      Lat = 28.6139m,  Lng = 77.2090m,  Districts = new[] { "Dwarka", "Rohini", "Janakpuri", "Lajpat Nagar", "Karol Bagh", "Saket", "Vasant Kunj", "Noida" } },
            new { Name = "Bangalore",  Lat = 12.9716m,  Lng = 77.5946m,  Districts = new[] { "Koramangala", "Indiranagar", "Whitefield", "HSR Layout", "JP Nagar", "Hebbal", "Electronic City", "Marathahalli" } },
            new { Name = "Hyderabad",  Lat = 17.3850m,  Lng = 78.4867m,  Districts = new[] { "Hitech City", "Gachibowli", "Banjara Hills", "Madhapur", "Secunderabad", "Kukatpally", "Ameerpet", "LB Nagar" } },
            new { Name = "Chennai",    Lat = 13.0827m,  Lng = 80.2707m,  Districts = new[] { "Anna Nagar", "T Nagar", "Adyar", "Velachery", "Porur", "Tambaram", "Sholinganallur", "Guindy" } },
            new { Name = "Pune",       Lat = 18.5204m,  Lng = 73.8567m,  Districts = new[] { "Kothrud", "Baner", "Hinjewadi", "Viman Nagar", "Hadapsar", "Kharadi", "Wakad", "Pimpri" } },
            new { Name = "Ahmedabad",  Lat = 23.0225m,  Lng = 72.5714m,  Districts = new[] { "Satellite", "Navrangpura", "Maninagar", "Bopal", "Chandkheda", "Gota", "Vastrapur", "CG Road" } },
            new { Name = "Surat",      Lat = 21.1702m,  Lng = 72.8311m,  Districts = new[] { "Adajan", "Athwa", "Katargam", "Varachha", "Althan", "Piplod", "City Light", "Vesu" } },
            new { Name = "Kolkata",    Lat = 22.5726m,  Lng = 88.3639m,  Districts = new[] { "Salt Lake", "Park Street", "New Town", "Howrah", "Dum Dum", "Jadavpur", "Garia", "Behala" } },
            new { Name = "Jaipur",     Lat = 26.9124m,  Lng = 75.7873m,  Districts = new[] { "Malviya Nagar", "Vaishali Nagar", "Mansarovar", "Sodala", "Jagatpura", "Civil Lines", "C Scheme", "Murlipura" } },
        };

        foreach (var c in cities)
        {
            var city = new City
            {
                Id = Guid.NewGuid(),
                Name = c.Name,
                Latitude = c.Lat,
                Longitude = c.Lng,
                CreatedAt = DateTime.UtcNow
            };
            db.Cities.Add(city);
            await db.SaveChangesAsync();

            var districts = c.Districts.Select(d => new District
            {
                Id = Guid.NewGuid(),
                CityId = city.Id,
                Name = d,
                Latitude = c.Lat + (decimal)(Random.Shared.NextDouble() * 0.1 - 0.05),
                Longitude = c.Lng + (decimal)(Random.Shared.NextDouble() * 0.1 - 0.05),
                CreatedAt = DateTime.UtcNow
            });
            db.Districts.AddRange(districts);
            await db.SaveChangesAsync();
        }
    }
}
