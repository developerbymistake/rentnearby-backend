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

    private static async Task SeedDistrictsAndCitiesAsync(ApplicationDbContext db)
    {
        if (await db.Districts.AnyAsync()) return;

        var data = new[]
        {
            new {
                Name = "Almora", Lat = 29.5971m, Lng = 79.6491m,
                Cities = new[] {
                    ("Almora",           29.5971m, 79.6491m),
                    ("Ranikhet",         29.6506m, 79.4553m),
                    ("Dwarahat",         29.5567m, 79.3881m),
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
                    ("Garur",       30.0433m, 79.9149m),
                    ("Kapkot",      29.8701m, 79.9502m),
                    ("Kanda",       29.8936m, 79.7254m),
                    ("Baijnath",    29.9200m, 79.6200m),
                    ("Kafligair",   30.0537m, 79.7651m),
                    ("Sarwar",      29.8609m, 79.7006m),
                    ("Bhalikhan",   29.8795m, 79.8500m),
                    ("Gagas",       29.9000m, 79.7000m),
                    ("Danda Dhura", 29.9800m, 79.8500m),
                }
            },
            new {
                Name = "Chamoli", Lat = 30.3660m, Lng = 79.3160m,
                Cities = new[] {
                    ("Gopeshwar",    30.3660m, 79.3160m),
                    ("Joshimath",    30.5688m, 79.5559m),
                    ("Karnaprayag",  30.2613m, 79.2377m),
                    ("Tharali",      30.2300m, 79.1500m),
                    ("Narayanbagar", 30.2650m, 79.0872m),
                    ("Gairsain",     30.0800m, 79.2800m),
                    ("Nandprayag",   30.3400m, 79.1900m),
                    ("Simli",        30.3000m, 79.1500m),
                    ("Pokhari",      30.3827m, 79.3190m),
                    ("Dewal",        30.1400m, 79.4600m),
                    ("Ghat",         30.2100m, 79.4200m),
                    ("Kulsari",      30.2900m, 79.4000m),
                }
            },
            new {
                Name = "Champawat", Lat = 29.2350m, Lng = 80.1023m,
                Cities = new[] {
                    ("Champawat",   29.2350m, 80.1023m),
                    ("Lohaghat",    29.4196m, 80.0661m),
                    ("Tanakpur",    29.5325m, 80.0405m),
                    ("Banbasa",     29.3632m, 79.8756m),
                    ("Barakot",     29.3839m, 80.0078m),
                    ("Pati",        29.2996m, 80.2024m),
                    ("Pancheshwar", 29.7019m, 80.6062m),
                    ("Devidhura",   29.7056m, 80.5246m),
                    ("Shuklaphanta",28.9200m, 80.0500m),
                    ("Purnagiri",   29.0500m, 80.0900m),
                }
            },
            new {
                Name = "Dehradun", Lat = 30.3256m, Lng = 78.0437m,
                Cities = new[] {
                    ("Dehradun",    30.3256m, 78.0437m),
                    ("Rishikesh",   30.0894m, 78.2680m),
                    ("Mussoorie",   30.4612m, 78.0747m),
                    ("Doiwala",     30.1769m, 78.1262m),
                    ("Vikasnagar",  29.9842m, 77.9931m),
                    ("Chakrata",    30.0833m, 77.8607m),
                    ("Sahaspur",    30.0882m, 78.2306m),
                    ("Raipur",      30.1519m, 78.4150m),
                    ("Herbertpur",  30.2053m, 78.5108m),
                    ("Selaqui",     30.3000m, 78.1500m),
                    ("Clement Town",30.2800m, 77.9900m),
                    ("Premnagar",   30.3299m, 78.2381m),
                    ("Raiwala",     30.3160m, 78.1969m),
                }
            },
            new {
                Name = "Haridwar", Lat = 29.9384m, Lng = 78.1453m,
                Cities = new[] {
                    ("Haridwar",   29.9384m, 78.1453m),
                    ("Roorkee",    29.8649m, 77.8945m),
                    ("Jwalapur",   29.9532m, 78.1699m),
                    ("Laksar",     29.9264m, 78.2455m),
                    ("Manglaur",   29.8883m, 78.2970m),
                    ("Bhagwanpur", 30.2528m, 78.2126m),
                    ("Bahadrabad", 29.9197m, 78.0437m),
                    ("Narsan",     29.7010m, 77.8482m),
                    ("Landhaura",  29.7987m, 77.9189m),
                    ("Mohand",     30.2373m, 77.9606m),
                    ("Pathri",     29.9800m, 78.0800m),
                    ("Khanpur",    29.8600m, 78.0600m),
                }
            },
            new {
                Name = "Nainital", Lat = 29.3803m, Lng = 79.4636m,
                Cities = new[] {
                    ("Nainital",   29.3803m, 79.4636m),
                    ("Haldwani",   29.2145m, 79.5279m),
                    ("Ramnagar",   29.3948m, 79.1269m),
                    ("Bhimtal",    29.3506m, 79.5542m),
                    ("Kaladhungi", 29.2846m, 79.3473m),
                    ("Mukteshwar", 29.4721m, 79.6480m),
                    ("Ramgarh",    29.4223m, 79.5512m),
                    ("Betalghat",  29.5387m, 79.3399m),
                    ("Lalkuan",    29.0679m, 79.5166m),
                    ("Dhari",      29.3167m, 79.7288m),
                    ("Okhalkanda", 29.4800m, 79.4800m),
                    ("Garampani",  29.3200m, 79.4000m),
                }
            },
            new {
                Name = "Pauri Garhwal", Lat = 30.1500m, Lng = 78.7700m,
                Cities = new[] {
                    ("Pauri",           30.1500m, 78.7700m),
                    ("Kotdwar",         29.7460m, 78.5201m),
                    ("Srinagar Garhwal",30.2206m, 78.7756m),
                    ("Lansdowne",       29.8378m, 78.6818m),
                    ("Satpuli",         29.8859m, 78.7651m),
                    ("Yamkeshwar",      29.9763m, 78.4183m),
                    ("Dugadda",         29.8070m, 78.6084m),
                    ("Dhumakot",        30.0000m, 78.7100m),
                    ("Bironkhal",       30.1900m, 79.0500m),
                    ("Thaliseain",      30.0600m, 78.9000m),
                    ("Ekeshwar",        30.1200m, 78.8300m),
                    ("Nainidanda",      29.9000m, 78.8200m),
                }
            },
            new {
                Name = "Pithoragarh", Lat = 29.5859m, Lng = 80.2152m,
                Cities = new[] {
                    ("Pithoragarh",  29.5859m, 80.2152m),
                    ("Dharchula",    29.8491m, 80.5415m),
                    ("Didihat",      29.7598m, 80.2498m),
                    ("Gangolihat",   29.6573m, 80.0400m),
                    ("Munsiyari",    30.1225m, 80.2415m),
                    ("Berinag",      29.7766m, 80.0531m),
                    ("Kanalichhina", 29.6768m, 80.2716m),
                    ("Thal",         30.0200m, 80.5000m),
                    ("Askot",        29.7646m, 80.3346m),
                    ("Narayanashram",29.9400m, 80.4400m),
                    ("Bungachhina",  29.6500m, 80.3000m),
                    ("Kapkot",       29.8100m, 80.0600m),
                }
            },
            new {
                Name = "Rudraprayag", Lat = 30.2847m, Lng = 78.9808m,
                Cities = new[] {
                    ("Rudraprayag", 30.2847m, 78.9808m),
                    ("Agastmuni",   30.3920m, 79.0260m),
                    ("Ukhimath",    30.5845m, 79.1288m),
                    ("Tilwara",     30.2400m, 79.0500m),
                    ("Guptkashi",   30.5283m, 79.0818m),
                    ("Bharasar",    30.2600m, 78.9500m),
                    ("Pokhri",      30.4600m, 79.0000m),
                    ("Kedarnath",   30.7339m, 79.0669m),
                    ("Silli",       30.3200m, 79.0200m),
                    ("Kund",        30.3800m, 79.0100m),
                }
            },
            new {
                Name = "Tehri Garhwal", Lat = 30.3929m, Lng = 78.4823m,
                Cities = new[] {
                    ("New Tehri",       30.3929m, 78.4823m),
                    ("Chamba",          30.3456m, 78.3943m),
                    ("Ghansali",        30.6039m, 78.7305m),
                    ("Dharasu",         30.6146m, 78.3147m),
                    ("Narendra Nagar",  30.1700m, 78.3000m),
                    ("Devprayag",       30.1461m, 78.5986m),
                    ("Kirtinagar",      30.2148m, 78.7484m),
                    ("Dunda",           30.6879m, 78.3549m),
                    ("Jakhnol",         30.4700m, 78.4500m),
                    ("Lambgaon",        30.3200m, 78.5800m),
                    ("Pratapnagar",     30.4900m, 78.5600m),
                    ("Muni Ki Reti",    30.1100m, 78.3200m),
                }
            },
            new {
                Name = "Udham Singh Nagar", Lat = 28.9707m, Lng = 79.3973m,
                Cities = new[] {
                    ("Rudrapur",    28.9707m, 79.3973m),
                    ("Kashipur",    29.2118m, 78.9617m),
                    ("Kichha",      28.9129m, 79.5197m),
                    ("Sitarganj",   28.9300m, 79.7000m),
                    ("Bajpur",      29.1613m, 79.1539m),
                    ("Gadarpur",    29.0424m, 79.2485m),
                    ("Jaspur",      29.2787m, 78.8277m),
                    ("Khatima",     28.9197m, 79.9703m),
                    ("Pantnagar",   29.0284m, 79.4832m),
                    ("Nanak Matta", 28.8700m, 79.6700m),
                    ("Dineshpur",   29.0496m, 79.3219m),
                    ("Bazpur",      29.1580m, 79.1467m),
                }
            },
            new {
                Name = "Uttarkashi", Lat = 30.7268m, Lng = 78.4354m,
                Cities = new[] {
                    ("Uttarkashi",   30.7268m, 78.4354m),
                    ("Bhatwari",     30.8600m, 78.5400m),
                    ("Dunda",        30.7900m, 78.5300m),
                    ("Mori",         31.1020m, 78.2039m),
                    ("Naugaon",      30.8500m, 78.0800m),
                    ("Purola",       30.8400m, 77.9000m),
                    ("Chinyalisaur", 30.5744m, 78.3277m),
                    ("Barkot",       30.8086m, 78.2060m),
                    ("Gangotri",     30.9944m, 78.9399m),
                    ("Harsil",       31.0369m, 78.7506m),
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
