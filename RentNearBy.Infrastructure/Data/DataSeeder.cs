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
            new RoomType { Id = Guid.NewGuid(), Name = "Single Room",   Description = "One unfurnished/semi-furnished room",           CreatedAt = DateTime.UtcNow },
            new RoomType { Id = Guid.NewGuid(), Name = "1 BHK",         Description = "1 bedroom, hall and kitchen apartment",         CreatedAt = DateTime.UtcNow },
            new RoomType { Id = Guid.NewGuid(), Name = "2 BHK",         Description = "2 bedroom, hall and kitchen apartment",         CreatedAt = DateTime.UtcNow },
            new RoomType { Id = Guid.NewGuid(), Name = "3 BHK",         Description = "3 bedroom, hall and kitchen apartment",         CreatedAt = DateTime.UtcNow },
            new RoomType { Id = Guid.NewGuid(), Name = "PG",            Description = "Paying guest accommodation",                   CreatedAt = DateTime.UtcNow },
            new RoomType { Id = Guid.NewGuid(), Name = "Studio",        Description = "Compact single-room apartment with kitchenette", CreatedAt = DateTime.UtcNow },
            new RoomType { Id = Guid.NewGuid(), Name = "Hostel",        Description = "Shared dormitory-style accommodation",          CreatedAt = DateTime.UtcNow },
            new RoomType { Id = Guid.NewGuid(), Name = "Shared Room",   Description = "Room shared with one or more people",           CreatedAt = DateTime.UtcNow },
        };

        db.RoomTypes.AddRange(roomTypes);
        await db.SaveChangesAsync();
    }

    private static async Task SeedCitiesAsync(ApplicationDbContext db)
    {
        if (await db.Cities.AnyAsync()) return;

        // All 13 districts of Uttarakhand as cities
        var cities = new[]
        {
            new {
                Name = "Nainital", Lat = 29.3803m, Lng = 79.4636m,
                Districts = new[] { "Nainital", "Haldwani", "Ramnagar", "Bhimtal", "Ramgarh", "Mukteshwar", "Betalghat", "Dhari" }
            },
            new {
                Name = "Dehradun", Lat = 30.3165m, Lng = 78.0322m,
                Districts = new[] { "Dehradun", "Rishikesh", "Mussoorie", "Doiwala", "Vikasnagar", "Chakrata", "Sahaspur", "Raipur" }
            },
            new {
                Name = "Haridwar", Lat = 29.9457m, Lng = 78.1642m,
                Districts = new[] { "Haridwar", "Roorkee", "Laksar", "Jwalapur", "Manglaur", "Bhagwanpur", "Bahadrabad", "Narsan" }
            },
            new {
                Name = "Almora", Lat = 29.5971m, Lng = 79.6491m,
                Districts = new[] { "Almora", "Ranikhet", "Dwarahat", "Salt", "Bhikiyasain", "Bhainsiyachhana", "Hawalbagh", "Tarikhet" }
            },
            new {
                Name = "Pauri Garhwal", Lat = 30.1500m, Lng = 78.7700m,
                Districts = new[] { "Pauri", "Kotdwar", "Srinagar", "Lansdowne", "Yamkeshwar", "Dhumakot", "Satpuli", "Dugadda" }
            },
            new {
                Name = "Tehri Garhwal", Lat = 30.3929m, Lng = 78.4823m,
                Districts = new[] { "New Tehri", "Chamba", "Ghansali", "Dharasu", "Narendra Nagar", "Pratapnagar", "Devprayag", "Kirtinagar" }
            },
            new {
                Name = "Chamoli", Lat = 30.3660m, Lng = 79.3160m,
                Districts = new[] { "Gopeshwar", "Joshimath", "Karnaprayag", "Tharali", "Narayanbagar", "Gairsain", "Simli", "Dewal" }
            },
            new {
                Name = "Uttarkashi", Lat = 30.7268m, Lng = 78.4354m,
                Districts = new[] { "Uttarkashi", "Bhatwari", "Dunda", "Mori", "Naugaon", "Purola", "Chinyalisaur", "Barkot" }
            },
            new {
                Name = "Rudraprayag", Lat = 30.2847m, Lng = 78.9808m,
                Districts = new[] { "Rudraprayag", "Agastmuni", "Ukhimath", "Tilwara", "Jakhnidhar", "Augustmuni", "Bharasar", "Pokhri" }
            },
            new {
                Name = "Pithoragarh", Lat = 29.5826m, Lng = 80.2177m,
                Districts = new[] { "Pithoragarh", "Dharchula", "Didihat", "Gangolihat", "Munsiyari", "Berinag", "Kanalichhina", "Thal" }
            },
            new {
                Name = "Udham Singh Nagar", Lat = 28.9981m, Lng = 79.4040m,
                Districts = new[] { "Rudrapur", "Kashipur", "Kichha", "Sitarganj", "Bajpur", "Gadarpur", "Jaspur", "Khatima" }
            },
            new {
                Name = "Bageshwar", Lat = 29.8360m, Lng = 79.7700m,
                Districts = new[] { "Bageshwar", "Garur", "Kapkot", "Bhalikhan", "Kanda", "Sarwar", "Kafligair", "Baijnath" }
            },
            new {
                Name = "Champawat", Lat = 29.3350m, Lng = 80.0900m,
                Districts = new[] { "Champawat", "Lohaghat", "Pati", "Barakot", "Banbasa", "Tanakpur", "Shuklaphanta", "Pancheshwar" }
            },
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
                Latitude = c.Lat + (decimal)(Random.Shared.NextDouble() * 0.15 - 0.075),
                Longitude = c.Lng + (decimal)(Random.Shared.NextDouble() * 0.15 - 0.075),
                CreatedAt = DateTime.UtcNow
            });
            db.Districts.AddRange(districts);
            await db.SaveChangesAsync();
        }
    }
}
