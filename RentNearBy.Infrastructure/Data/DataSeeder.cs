using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;

namespace RentNearBy.Infrastructure.Data;

public static class DataSeeder
{
    public static async Task SeedAsync(ApplicationDbContext db)
    {
        await SeedRoomTypesAsync(db);
        await SeedDistrictsAndCitiesAsync(db);
    }

    private static async Task SeedRoomTypesAsync(ApplicationDbContext db)
    {
        if (await db.RoomTypes.AnyAsync()) return;

        var roomTypes = new[]
        {
            new RoomType { Id = Guid.NewGuid(), Name = "Single Room",  Description = "One unfurnished/semi-furnished room",            CreatedAt = DateTime.UtcNow },
            new RoomType { Id = Guid.NewGuid(), Name = "1 BHK",        Description = "1 bedroom, hall and kitchen apartment",          CreatedAt = DateTime.UtcNow },
            new RoomType { Id = Guid.NewGuid(), Name = "2 BHK",        Description = "2 bedroom, hall and kitchen apartment",          CreatedAt = DateTime.UtcNow },
            new RoomType { Id = Guid.NewGuid(), Name = "3 BHK",        Description = "3 bedroom, hall and kitchen apartment",          CreatedAt = DateTime.UtcNow },
            new RoomType { Id = Guid.NewGuid(), Name = "PG",           Description = "Paying guest accommodation",                    CreatedAt = DateTime.UtcNow },
            new RoomType { Id = Guid.NewGuid(), Name = "Studio",       Description = "Compact single-room apartment with kitchenette", CreatedAt = DateTime.UtcNow },
            new RoomType { Id = Guid.NewGuid(), Name = "Hostel",       Description = "Shared dormitory-style accommodation",           CreatedAt = DateTime.UtcNow },
            new RoomType { Id = Guid.NewGuid(), Name = "Shared Room",  Description = "Room shared with one or more people",            CreatedAt = DateTime.UtcNow },
        };

        db.RoomTypes.AddRange(roomTypes);
        await db.SaveChangesAsync();
    }

    private static async Task SeedDistrictsAndCitiesAsync(ApplicationDbContext db)
    {
        if (await db.Districts.AnyAsync()) return;

        // All 13 districts of Uttarakhand with their major cities/towns
        // Coordinates are accurate geographic centroids / headquarters locations
        var data = new[]
        {
            new {
                Name = "Almora", Lat = 29.5971m, Lng = 79.6491m,
                Cities = new[] {
                    ("Almora",           29.5971m, 79.6491m),
                    ("Ranikhet",         29.6458m, 79.4326m),
                    ("Dwarahat",         29.7050m, 79.4053m),
                    ("Salt",             29.7546m, 79.2126m),
                    ("Bhikiyasain",      29.6055m, 79.2990m),
                    ("Hawalbagh",        29.6195m, 79.5989m),
                    ("Tarikhet",         29.7255m, 79.5002m),
                    ("Chaukhutia",       29.8195m, 79.3783m),
                    ("Bhainsiyachhana",  29.6495m, 79.5019m),
                    ("Syalde",           29.6800m, 79.3500m),
                    ("Lamgara",          29.7600m, 79.5600m),
                    ("Someshwar",        29.6900m, 79.5400m),
                }
            },
            new {
                Name = "Bageshwar", Lat = 29.8360m, Lng = 79.7700m,
                Cities = new[] {
                    ("Bageshwar",   29.8360m, 79.7700m),
                    ("Garur",       29.9633m, 79.6812m),
                    ("Kapkot",      30.0220m, 79.9588m),
                    ("Kanda",       29.8936m, 79.7254m),
                    ("Baijnath",    29.9200m, 79.6200m),
                    ("Kafligair",   29.9183m, 79.7508m),
                    ("Sarwar",      29.8609m, 79.7006m),
                    ("Bhalikhan",   29.8795m, 79.8500m),
                    ("Gagas",       29.9000m, 79.7000m),
                    ("Danda Dhura", 29.9800m, 79.8500m),
                }
            },
            new {
                Name = "Chamoli", Lat = 30.4072m, Lng = 79.3258m,
                Cities = new[] {
                    ("Gopeshwar",    30.3660m, 79.3160m),
                    ("Joshimath",    30.5578m, 79.5637m),
                    ("Karnaprayag",  30.2613m, 79.2377m),
                    ("Tharali",      30.2300m, 79.1500m),
                    ("Narayanbagar", 30.1700m, 79.0965m),
                    ("Gairsain",     30.0800m, 79.2800m),
                    ("Nandprayag",   30.3400m, 79.1900m),
                    ("Simli",        30.3000m, 79.1500m),
                    ("Pokhari",      30.3500m, 79.2500m),
                    ("Dewal",        30.1400m, 79.4600m),
                    ("Ghat",         30.2100m, 79.4200m),
                    ("Kulsari",      30.2900m, 79.4000m),
                }
            },
            new {
                Name = "Champawat", Lat = 29.3350m, Lng = 80.0900m,
                Cities = new[] {
                    ("Champawat",  29.3350m, 80.0900m),
                    ("Lohaghat",   29.4100m, 80.0700m),
                    ("Tanakpur",   28.9600m, 80.1100m),
                    ("Banbasa",    28.9700m, 79.9900m),
                    ("Barakot",    29.5000m, 80.1000m),
                    ("Pati",       29.3800m, 80.1500m),
                    ("Pancheshwar",29.4600m, 80.4000m),
                    ("Devidhura",  29.3700m, 79.9200m),
                    ("Shuklaphanta",28.9200m, 80.0500m),
                    ("Purnagiri",  29.0500m, 80.0900m),
                }
            },
            new {
                Name = "Dehradun", Lat = 30.3165m, Lng = 78.0322m,
                Cities = new[] {
                    ("Dehradun",    30.3165m, 78.0322m),
                    ("Rishikesh",   30.0869m, 78.2676m),
                    ("Mussoorie",   30.4598m, 78.0644m),
                    ("Doiwala",     30.1900m, 78.1200m),
                    ("Vikasnagar",  30.4300m, 77.7700m),
                    ("Chakrata",    30.7000m, 77.8700m),
                    ("Sahaspur",    30.2200m, 78.1100m),
                    ("Raipur",      30.3400m, 78.1100m),
                    ("Herbertpur",  30.3800m, 77.8300m),
                    ("Selaqui",     30.3800m, 77.9200m),
                    ("Clement Town",30.2800m, 77.9900m),
                    ("Premnagar",   30.2600m, 78.0100m),
                    ("Raiwala",     30.1500m, 78.1900m),
                }
            },
            new {
                Name = "Haridwar", Lat = 29.9457m, Lng = 78.1642m,
                Cities = new[] {
                    ("Haridwar",   29.9457m, 78.1642m),
                    ("Roorkee",    29.8543m, 77.8880m),
                    ("Jwalapur",   29.8900m, 78.1200m),
                    ("Laksar",     29.7400m, 78.0400m),
                    ("Manglaur",   29.7800m, 77.8700m),
                    ("Bhagwanpur", 29.9200m, 77.9000m),
                    ("Bahadrabad", 29.9600m, 78.0200m),
                    ("Narsan",     29.8200m, 77.9300m),
                    ("Landhaura",  29.8800m, 77.9300m),
                    ("Mohand",     30.0700m, 77.9200m),
                    ("Pathri",     29.9800m, 78.0800m),
                    ("Khanpur",    29.8600m, 78.0600m),
                }
            },
            new {
                Name = "Nainital", Lat = 29.3803m, Lng = 79.4636m,
                Cities = new[] {
                    ("Nainital",   29.3803m, 79.4636m),
                    ("Haldwani",   29.2183m, 79.5130m),
                    ("Ramnagar",   29.3953m, 79.1290m),
                    ("Bhimtal",    29.3479m, 79.5618m),
                    ("Kaladhungi", 29.2700m, 79.3400m),
                    ("Mukteshwar", 29.4700m, 79.6400m),
                    ("Ramgarh",    29.4200m, 79.5500m),
                    ("Betalghat",  29.4400m, 79.2700m),
                    ("Lalkuan",    29.0800m, 79.5000m),
                    ("Dhari",      29.2900m, 79.4200m),
                    ("Okhalkanda", 29.4800m, 79.4800m),
                    ("Garampani",  29.3200m, 79.4000m),
                }
            },
            new {
                Name = "Pauri Garhwal", Lat = 30.1500m, Lng = 78.7700m,
                Cities = new[] {
                    ("Pauri",           30.1500m, 78.7700m),
                    ("Kotdwar",         29.7500m, 78.5300m),
                    ("Srinagar Garhwal",30.2200m, 78.7800m),
                    ("Lansdowne",       29.8400m, 78.6800m),
                    ("Satpuli",         29.9400m, 78.6200m),
                    ("Yamkeshwar",      30.0100m, 78.5700m),
                    ("Dugadda",         29.7900m, 78.6400m),
                    ("Dhumakot",        30.0000m, 78.7100m),
                    ("Bironkhal",       30.1900m, 79.0500m),
                    ("Thaliseain",      30.0600m, 78.9000m),
                    ("Ekeshwar",        30.1200m, 78.8300m),
                    ("Nainidanda",      29.9000m, 78.8200m),
                }
            },
            new {
                Name = "Pithoragarh", Lat = 29.5826m, Lng = 80.2177m,
                Cities = new[] {
                    ("Pithoragarh",  29.5826m, 80.2177m),
                    ("Dharchula",    29.8600m, 80.5300m),
                    ("Didihat",      29.7900m, 80.3500m),
                    ("Gangolihat",   29.7400m, 80.0100m),
                    ("Munsiyari",    30.0700m, 80.2300m),
                    ("Berinag",      29.7200m, 80.1300m),
                    ("Kanalichhina", 29.6800m, 80.1800m),
                    ("Thal",         30.0200m, 80.5000m),
                    ("Askot",        29.7900m, 80.4600m),
                    ("Narayanashram",29.9400m, 80.4400m),
                    ("Bungachhina",  29.6500m, 80.3000m),
                    ("Kapkot",       29.8100m, 80.0600m),
                }
            },
            new {
                Name = "Rudraprayag", Lat = 30.2847m, Lng = 78.9808m,
                Cities = new[] {
                    ("Rudraprayag", 30.2847m, 78.9808m),
                    ("Agastmuni",   30.3400m, 78.9900m),
                    ("Ukhimath",    30.5000m, 79.0400m),
                    ("Tilwara",     30.2400m, 79.0500m),
                    ("Guptkashi",   30.5276m, 79.0763m),
                    ("Bharasar",    30.2600m, 78.9500m),
                    ("Pokhri",      30.4600m, 79.0000m),
                    ("Kedarnath",   30.7352m, 79.0669m),
                    ("Silli",       30.3200m, 79.0200m),
                    ("Kund",        30.3800m, 79.0100m),
                }
            },
            new {
                Name = "Tehri Garhwal", Lat = 30.3929m, Lng = 78.4823m,
                Cities = new[] {
                    ("New Tehri",       30.3929m, 78.4823m),
                    ("Chamba",          30.3500m, 78.2500m),
                    ("Ghansali",        30.4300m, 78.6800m),
                    ("Dharasu",         30.6000m, 78.2800m),
                    ("Narendra Nagar",  30.1700m, 78.3000m),
                    ("Devprayag",       30.1455m, 78.5979m),
                    ("Kirtinagar",      30.2600m, 78.5600m),
                    ("Dunda",           30.5800m, 78.3200m),
                    ("Jakhnol",         30.4700m, 78.4500m),
                    ("Lambgaon",        30.3200m, 78.5800m),
                    ("Pratapnagar",     30.4900m, 78.5600m),
                    ("Muni Ki Reti",    30.1100m, 78.3200m),
                }
            },
            new {
                Name = "Udham Singh Nagar", Lat = 28.9814m, Lng = 79.3998m,
                Cities = new[] {
                    ("Rudrapur",    28.9814m, 79.3998m),
                    ("Kashipur",    29.2100m, 78.9600m),
                    ("Kichha",      28.9100m, 79.4800m),
                    ("Sitarganj",   28.9300m, 79.7000m),
                    ("Bajpur",      29.0700m, 79.0800m),
                    ("Gadarpur",    29.0200m, 79.2800m),
                    ("Jaspur",      29.2800m, 78.8300m),
                    ("Khatima",     28.9200m, 79.9700m),
                    ("Pantnagar",   29.0300m, 79.4800m),
                    ("Nanak Matta", 28.8700m, 79.6700m),
                    ("Dineshpur",   28.9900m, 79.2000m),
                    ("Bazpur",      29.1600m, 79.1100m),
                }
            },
            new {
                Name = "Uttarkashi", Lat = 30.7268m, Lng = 78.4354m,
                Cities = new[] {
                    ("Uttarkashi",   30.7268m, 78.4354m),
                    ("Bhatwari",     30.8600m, 78.5400m),
                    ("Dunda",        30.7900m, 78.5300m),
                    ("Mori",         30.9400m, 77.9500m),
                    ("Naugaon",      30.8500m, 78.0800m),
                    ("Purola",       30.8400m, 77.9000m),
                    ("Chinyalisaur", 30.6800m, 78.3700m),
                    ("Barkot",       30.8000m, 78.0500m),
                    ("Gangotri",     30.9951m, 78.9397m),
                    ("Harsil",       31.0800m, 78.7700m),
                    ("Dharali",      31.0200m, 78.8400m),
                    ("Maneri",       30.7700m, 78.5000m),
                }
            },
        };

        foreach (var d in data)
        {
            var district = new District
            {
                Id = Guid.NewGuid(),
                Name = d.Name,
                Latitude = d.Lat,
                Longitude = d.Lng,
                CreatedAt = DateTime.UtcNow
            };
            db.Districts.Add(district);
            await db.SaveChangesAsync();

            var cities = d.Cities.Select(c => new City
            {
                Id = Guid.NewGuid(),
                DistrictId = district.Id,
                Name = c.Item1,
                Latitude = c.Item2,
                Longitude = c.Item3,
                CreatedAt = DateTime.UtcNow
            });
            db.Cities.AddRange(cities);
            await db.SaveChangesAsync();
        }
    }
}
