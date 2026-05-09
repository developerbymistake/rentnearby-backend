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
            // ── UTTARAKHAND ──────────────────────────────────────────────────────────
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

            // ── HARYANA ──────────────────────────────────────────────────────────────
            new {
                Name = "Ambala", Lat = 30.3752m, Lng = 76.7821m,
                Cities = new[] {
                    ("Ambala",              30.3752m, 76.7821m),
                    ("Ambala Cantonment",   30.3293m, 76.8192m),
                    ("Naraingarh",          30.4575m, 77.0945m),
                    ("Mullana",             30.3985m, 77.0494m),
                    ("Barara",              30.3011m, 77.1823m),
                }
            },
            new {
                Name = "Bhiwani", Lat = 28.7932m, Lng = 76.1323m,
                Cities = new[] {
                    ("Bhiwani",      28.7932m, 76.1323m),
                    ("Loharu",       28.4413m, 75.8066m),
                    ("Tosham",       28.5922m, 75.9219m),
                    ("Siwani",       28.9556m, 75.6768m),
                    ("Bawani Khera", 28.8500m, 76.0500m),
                }
            },
            new {
                Name = "Charkhi Dadri", Lat = 28.5921m, Lng = 76.2688m,
                Cities = new[] {
                    ("Charkhi Dadri", 28.5921m, 76.2688m),
                    ("Badhra",        28.4459m, 76.0434m),
                    ("Jhojhu Kalan",  28.6614m, 76.1823m),
                }
            },
            new {
                Name = "Faridabad", Lat = 28.4089m, Lng = 77.3178m,
                Cities = new[] {
                    ("Faridabad",    28.4089m, 77.3178m),
                    ("Ballabhgarh",  28.3387m, 77.3188m),
                    ("Palwal",       28.1429m, 77.3240m),
                    ("Hodal",        27.8956m, 77.3643m),
                }
            },
            new {
                Name = "Fatehabad", Lat = 29.5153m, Lng = 75.4549m,
                Cities = new[] {
                    ("Fatehabad", 29.5153m, 75.4549m),
                    ("Ratia",     29.6847m, 75.5721m),
                    ("Tohana",    29.6989m, 75.9055m),
                    ("Jakhal",    29.7924m, 75.8218m),
                }
            },
            new {
                Name = "Gurugram", Lat = 28.4595m, Lng = 77.0266m,
                Cities = new[] {
                    ("Gurugram",    28.4595m, 77.0266m),
                    ("Sohna",       28.2477m, 77.0708m),
                    ("Pataudi",     28.3219m, 76.7956m),
                    ("Manesar",     28.3556m, 76.9321m),
                    ("Farukhnagar", 28.4542m, 76.8187m),
                }
            },
            new {
                Name = "Hisar", Lat = 29.1500m, Lng = 75.7228m,
                Cities = new[] {
                    ("Hisar",   29.1500m, 75.7228m),
                    ("Hansi",   29.1003m, 75.9656m),
                    ("Uklana",  29.4823m, 75.9202m),
                    ("Barwala", 29.3611m, 75.8894m),
                    ("Adampur", 29.0632m, 75.7688m),
                }
            },
            new {
                Name = "Jhajjar", Lat = 28.6100m, Lng = 76.6548m,
                Cities = new[] {
                    ("Jhajjar",    28.6100m, 76.6548m),
                    ("Bahadurgarh",28.6821m, 76.9357m),
                    ("Beri",       28.7115m, 76.5819m),
                    ("Machhrauli", 28.5002m, 76.7823m),
                }
            },
            new {
                Name = "Jind", Lat = 29.3161m, Lng = 76.3187m,
                Cities = new[] {
                    ("Jind",    29.3161m, 76.3187m),
                    ("Narwana", 29.6019m, 76.1106m),
                    ("Safidon", 29.4077m, 76.6732m),
                    ("Julana",  29.4273m, 76.3046m),
                    ("Uchana",  29.4793m, 76.3142m),
                }
            },
            new {
                Name = "Kaithal", Lat = 29.8014m, Lng = 76.3995m,
                Cities = new[] {
                    ("Kaithal", 29.8014m, 76.3995m),
                    ("Cheeka",  29.6969m, 76.1518m),
                    ("Kalayat", 29.7317m, 76.5612m),
                    ("Pundri",  29.7530m, 76.5499m),
                    ("Guhla",   29.9248m, 76.3213m),
                }
            },
            new {
                Name = "Karnal", Lat = 29.6857m, Lng = 76.9905m,
                Cities = new[] {
                    ("Karnal",    29.6857m, 76.9905m),
                    ("Panipat",   29.3909m, 76.9635m),
                    ("Assandh",   29.5010m, 76.5693m),
                    ("Gharaunda", 29.5420m, 76.9740m),
                    ("Nilokheri", 29.8445m, 76.9437m),
                }
            },
            new {
                Name = "Kurukshetra", Lat = 29.9695m, Lng = 76.8783m,
                Cities = new[] {
                    ("Kurukshetra", 29.9695m, 76.8783m),
                    ("Thanesar",    29.9726m, 76.8385m),
                    ("Shahabad",    30.1630m, 76.9173m),
                    ("Pehowa",      29.9827m, 76.5870m),
                    ("Ladwa",       29.9993m, 77.0497m),
                }
            },
            new {
                Name = "Mahendragarh", Lat = 28.0479m, Lng = 76.1085m,
                Cities = new[] {
                    ("Narnaul",          28.0479m, 76.1085m),
                    ("Mahendragarh",     28.2755m, 76.1490m),
                    ("Ateli",            28.1197m, 76.0474m),
                    ("Nangal Chaudhary", 28.0540m, 76.2353m),
                }
            },
            new {
                Name = "Nuh", Lat = 28.0954m, Lng = 77.0007m,
                Cities = new[] {
                    ("Nuh",               28.0954m, 77.0007m),
                    ("Punhana",           28.1316m, 77.1155m),
                    ("Ferozepur Jhirka",  27.9886m, 77.0450m),
                    ("Tauru",             28.2303m, 77.0070m),
                }
            },
            new {
                Name = "Palwal", Lat = 28.1429m, Lng = 77.3240m,
                Cities = new[] {
                    ("Palwal",    28.1429m, 77.3240m),
                    ("Hodal",     27.8956m, 77.3643m),
                    ("Hathin",    27.9937m, 77.2626m),
                    ("Hassanpur", 28.0643m, 77.2791m),
                }
            },
            new {
                Name = "Panchkula", Lat = 30.6942m, Lng = 76.8606m,
                Cities = new[] {
                    ("Panchkula",   30.6942m, 76.8606m),
                    ("Kalka",       30.8451m, 76.9457m),
                    ("Morni",       30.7037m, 77.1147m),
                    ("Raipur Rani", 30.5459m, 77.0523m),
                    ("Pinjore",     30.7980m, 76.9194m),
                }
            },
            new {
                Name = "Panipat", Lat = 29.3909m, Lng = 76.9635m,
                Cities = new[] {
                    ("Panipat",  29.3909m, 76.9635m),
                    ("Samalkha", 29.2381m, 77.0138m),
                    ("Israna",   29.4622m, 76.8527m),
                    ("Madlauda", 29.4774m, 77.0613m),
                }
            },
            new {
                Name = "Rewari", Lat = 28.1974m, Lng = 76.6187m,
                Cities = new[] {
                    ("Rewari",    28.1974m, 76.6187m),
                    ("Bawal",     28.0706m, 76.5787m),
                    ("Dharuhera", 28.2046m, 76.7927m),
                    ("Kosli",     28.4007m, 76.5249m),
                }
            },
            new {
                Name = "Rohtak", Lat = 28.8955m, Lng = 76.6066m,
                Cities = new[] {
                    ("Rohtak",       28.8955m, 76.6066m),
                    ("Asthal Bohar", 28.8407m, 76.7012m),
                    ("Lakhan Majra", 28.9655m, 76.5148m),
                    ("Sampla",       28.8327m, 76.8281m),
                }
            },
            new {
                Name = "Sirsa", Lat = 29.5328m, Lng = 75.0313m,
                Cities = new[] {
                    ("Sirsa",      29.5328m, 75.0313m),
                    ("Dabwali",    29.9768m, 74.7258m),
                    ("Ellenabad",  29.4520m, 74.6607m),
                    ("Rania",      29.5398m, 74.8396m),
                }
            },
            new {
                Name = "Sonipat", Lat = 28.9945m, Lng = 77.0151m,
                Cities = new[] {
                    ("Sonipat",  28.9945m, 77.0151m),
                    ("Gohana",   29.1262m, 76.7041m),
                    ("Ganaur",   29.1801m, 77.0125m),
                    ("Kharkhoda",28.9230m, 76.9091m),
                    ("Rai",      28.9696m, 77.0972m),
                }
            },
            new {
                Name = "Yamunanagar", Lat = 30.1290m, Lng = 77.2674m,
                Cities = new[] {
                    ("Yamunanagar",  30.1290m, 77.2674m),
                    ("Jagadhri",     30.1673m, 77.3003m),
                    ("Radaur",       30.0426m, 77.2011m),
                    ("Bilaspur",     30.2616m, 77.3192m),
                    ("Chhachhrauli", 30.2527m, 77.4011m),
                }
            },

            // ── UTTAR PRADESH ────────────────────────────────────────────────────────
            new {
                Name = "Agra", Lat = 27.1767m, Lng = 78.0081m,
                Cities = new[] {
                    ("Agra",       27.1767m, 78.0081m),
                    ("Firozabad",  27.1522m, 78.3953m),
                    ("Fatehabad",  27.1116m, 78.2370m),
                    ("Kheragarh", 27.0561m, 78.5536m),
                    ("Bah",        26.8810m, 78.5930m),
                }
            },
            new {
                Name = "Aligarh", Lat = 27.8974m, Lng = 78.0880m,
                Cities = new[] {
                    ("Aligarh", 27.8974m, 78.0880m),
                    ("Hathras", 27.5940m, 78.0533m),
                    ("Iglas",   27.7161m, 77.9467m),
                    ("Atrauli", 28.0407m, 78.2687m),
                    ("Khair",   27.9385m, 77.8435m),
                }
            },
            new {
                Name = "Ambedkar Nagar", Lat = 26.4315m, Lng = 82.5360m,
                Cities = new[] {
                    ("Akbarpur", 26.4315m, 82.5360m),
                    ("Tanda",    26.5613m, 82.5927m),
                    ("Jalalpur", 26.3274m, 82.7077m),
                    ("Allapur",  26.3912m, 82.4509m),
                }
            },
            new {
                Name = "Amethi", Lat = 26.1486m, Lng = 81.7007m,
                Cities = new[] {
                    ("Amethi",       26.1486m, 81.7007m),
                    ("Gauriganj",    26.1967m, 81.8194m),
                    ("Musafirkhana", 26.3570m, 81.8006m),
                    ("Salon",        26.0316m, 81.4476m),
                }
            },
            new {
                Name = "Amroha", Lat = 28.9045m, Lng = 78.4676m,
                Cities = new[] {
                    ("Amroha",   28.9045m, 78.4676m),
                    ("Hasanpur", 28.7222m, 78.2792m),
                    ("Dhanaura", 28.9514m, 78.2416m),
                    ("Gajraula", 28.8295m, 78.2786m),
                }
            },
            new {
                Name = "Auraiya", Lat = 26.4673m, Lng = 79.5098m,
                Cities = new[] {
                    ("Auraiya",  26.4673m, 79.5098m),
                    ("Dibiyapur",26.5694m, 79.5295m),
                    ("Bidhuna",  26.8016m, 79.5063m),
                    ("Ajitmal",  26.6165m, 79.3530m),
                }
            },
            new {
                Name = "Ayodhya", Lat = 26.7922m, Lng = 82.1942m,
                Cities = new[] {
                    ("Ayodhya",  26.7922m, 82.1942m),
                    ("Faizabad", 26.7751m, 82.1466m),
                    ("Bikapur",  26.6584m, 82.1156m),
                    ("Sohawal",  26.6809m, 82.3029m),
                    ("Milkipur", 26.5821m, 82.1519m),
                }
            },
            new {
                Name = "Azamgarh", Lat = 26.0735m, Lng = 83.1857m,
                Cities = new[] {
                    ("Azamgarh", 26.0735m, 83.1857m),
                    ("Lalganj",  25.8739m, 83.4132m),
                    ("Sagri",    26.0451m, 83.0705m),
                    ("Nizamabad",25.6913m, 83.7814m),
                    ("Phulpur",  26.0800m, 83.0500m),
                }
            },
            new {
                Name = "Baghpat", Lat = 28.9484m, Lng = 77.2165m,
                Cities = new[] {
                    ("Baghpat", 28.9484m, 77.2165m),
                    ("Baraut",  29.0988m, 77.2645m),
                    ("Khekra",  28.8612m, 77.2773m),
                    ("Pilana",  29.0025m, 77.1469m),
                }
            },
            new {
                Name = "Bahraich", Lat = 27.5741m, Lng = 81.5941m,
                Cities = new[] {
                    ("Bahraich",  27.5741m, 81.5941m),
                    ("Nanpara",   27.8655m, 81.4989m),
                    ("Kaiserganj",27.6626m, 81.6685m),
                    ("Mahsi",     27.7164m, 81.7742m),
                    ("Jarwal",    27.3793m, 81.4793m),
                }
            },
            new {
                Name = "Ballia", Lat = 25.7598m, Lng = 84.1474m,
                Cities = new[] {
                    ("Ballia",      25.7598m, 84.1474m),
                    ("Rasra",       25.8512m, 83.8517m),
                    ("Bansdih",     25.8771m, 84.2210m),
                    ("Sikandarpur", 26.0379m, 84.0617m),
                    ("Bairia",      26.0040m, 84.0175m),
                }
            },
            new {
                Name = "Balrampur", Lat = 27.4310m, Lng = 82.1826m,
                Cities = new[] {
                    ("Balrampur", 27.4310m, 82.1826m),
                    ("Tulsipur",  27.5311m, 82.4076m),
                    ("Utraula",   27.3236m, 82.4192m),
                    ("Gaisdi",    27.4648m, 82.1253m),
                }
            },
            new {
                Name = "Banda", Lat = 25.4764m, Lng = 80.3346m,
                Cities = new[] {
                    ("Banda",    25.4764m, 80.3346m),
                    ("Atarrha",  25.3831m, 80.4234m),
                    ("Naraini",  25.2080m, 80.4789m),
                    ("Baberu",   25.5341m, 80.6851m),
                    ("Tindwari", 25.5543m, 80.5048m),
                }
            },
            new {
                Name = "Barabanki", Lat = 26.9277m, Lng = 81.1848m,
                Cities = new[] {
                    ("Barabanki",  26.9277m, 81.1848m),
                    ("Haidergarh", 26.8048m, 81.4388m),
                    ("Ram Nagar",  26.9157m, 81.3501m),
                    ("Nawabganj",  26.9323m, 81.2471m),
                    ("Ramsanehihat",26.8900m, 81.5200m),
                }
            },
            new {
                Name = "Bareilly", Lat = 28.3670m, Lng = 79.4304m,
                Cities = new[] {
                    ("Bareilly",  28.3670m, 79.4304m),
                    ("Pilibhit",  28.6311m, 79.8047m),
                    ("Baheri",    28.7586m, 79.4912m),
                    ("Aonla",     28.3985m, 79.3862m),
                    ("Fatehganj", 28.4543m, 79.3501m),
                }
            },
            new {
                Name = "Basti", Lat = 26.8009m, Lng = 82.7289m,
                Cities = new[] {
                    ("Basti",      26.8009m, 82.7289m),
                    ("Harraiya",   26.9058m, 82.9677m),
                    ("Kaptanganj", 26.9119m, 83.0564m),
                    ("Bhanpur",    26.8540m, 82.6978m),
                }
            },
            new {
                Name = "Bhadohi", Lat = 25.3926m, Lng = 82.5672m,
                Cities = new[] {
                    ("Bhadohi",   25.3926m, 82.5672m),
                    ("Gyanpur",   25.3517m, 82.5946m),
                    ("Aurai",     25.3084m, 82.7152m),
                    ("Suriyawan", 25.3474m, 82.6427m),
                }
            },
            new {
                Name = "Bijnor", Lat = 29.3722m, Lng = 78.1355m,
                Cities = new[] {
                    ("Bijnor",    29.3722m, 78.1355m),
                    ("Nagina",    29.4433m, 78.4372m),
                    ("Najibabad", 29.6133m, 78.3427m),
                    ("Chandpur",  29.3041m, 78.2726m),
                    ("Kiratpur",  29.5394m, 78.5147m),
                }
            },
            new {
                Name = "Budaun", Lat = 28.0392m, Lng = 79.1269m,
                Cities = new[] {
                    ("Budaun",   28.0392m, 79.1269m),
                    ("Bisauli",  28.2865m, 78.9701m),
                    ("Sahaswan", 28.0726m, 78.7521m),
                    ("Dataganj", 28.1965m, 79.4619m),
                    ("Ujhani",   28.0013m, 79.0157m),
                }
            },
            new {
                Name = "Bulandshahr", Lat = 28.4079m, Lng = 77.8497m,
                Cities = new[] {
                    ("Bulandshahr", 28.4079m, 77.8497m),
                    ("Khurja",      28.2578m, 77.8548m),
                    ("Sikandrabad", 28.4511m, 77.6938m),
                    ("Anupshahr",   28.3699m, 78.2776m),
                    ("Shikarpur",   28.2824m, 78.0083m),
                }
            },
            new {
                Name = "Chandauli", Lat = 25.2706m, Lng = 83.2693m,
                Cities = new[] {
                    ("Chandauli",  25.2706m, 83.2693m),
                    ("Mughal Sarai",25.2816m, 83.1163m),
                    ("Sakaldiha",  25.3671m, 83.0759m),
                    ("Chahaniya",  25.1953m, 83.3826m),
                }
            },
            new {
                Name = "Chitrakoot", Lat = 25.1994m, Lng = 80.8978m,
                Cities = new[] {
                    ("Chitrakoot", 25.1994m, 80.8978m),
                    ("Karwi",      25.2025m, 80.9011m),
                    ("Manikpur",   25.0631m, 81.1024m),
                    ("Mau",        25.2800m, 80.8500m),
                }
            },
            new {
                Name = "Deoria", Lat = 26.5044m, Lng = 83.7840m,
                Cities = new[] {
                    ("Deoria",       26.5044m, 83.7840m),
                    ("Bhatpar Rani", 26.3023m, 83.9749m),
                    ("Salempur",     26.3012m, 83.8754m),
                    ("Barhaj",       26.2643m, 83.8098m),
                    ("Rudrapur",     26.3800m, 83.9600m),
                }
            },
            new {
                Name = "Etah", Lat = 27.5641m, Lng = 78.6638m,
                Cities = new[] {
                    ("Etah",    27.5641m, 78.6638m),
                    ("Kasganj", 27.8089m, 78.6484m),
                    ("Jalesar", 27.4605m, 78.8005m),
                    ("Aliganj", 27.4946m, 79.1797m),
                    ("Patiali", 27.6942m, 79.0154m),
                }
            },
            new {
                Name = "Etawah", Lat = 26.7828m, Lng = 79.0247m,
                Cities = new[] {
                    ("Etawah",      26.7828m, 79.0247m),
                    ("Jaswantnagar",26.8935m, 79.2316m),
                    ("Bharthana",   26.7399m, 79.3052m),
                    ("Saifai",      26.9887m, 79.2843m),
                }
            },
            new {
                Name = "Farrukhabad", Lat = 27.3904m, Lng = 79.5799m,
                Cities = new[] {
                    ("Farrukhabad", 27.3904m, 79.5799m),
                    ("Fatehgarh",   27.3661m, 79.6326m),
                    ("Kaimganj",    27.5612m, 79.3456m),
                    ("Shamsabad",   27.3456m, 79.7113m),
                }
            },
            new {
                Name = "Fatehpur", Lat = 25.9280m, Lng = 80.8124m,
                Cities = new[] {
                    ("Fatehpur",   25.9280m, 80.8124m),
                    ("Bindki",     25.9822m, 80.5799m),
                    ("Khaga",      25.7749m, 81.0290m),
                    ("Hussainganj",25.9010m, 80.8952m),
                }
            },
            new {
                Name = "Firozabad", Lat = 27.1522m, Lng = 78.3953m,
                Cities = new[] {
                    ("Firozabad",  27.1522m, 78.3953m),
                    ("Shikohabad", 27.1074m, 78.5897m),
                    ("Tundla",     27.2104m, 78.2427m),
                    ("Jasrana",    27.2447m, 78.6530m),
                }
            },
            new {
                Name = "Gautam Buddha Nagar", Lat = 28.5355m, Lng = 77.3910m,
                Cities = new[] {
                    ("Noida",        28.5355m, 77.3910m),
                    ("Greater Noida",28.4744m, 77.5040m),
                    ("Dadri",        28.5557m, 77.5533m),
                    ("Jewar",        28.1237m, 77.5547m),
                }
            },
            new {
                Name = "Ghaziabad", Lat = 28.6692m, Lng = 77.4538m,
                Cities = new[] {
                    ("Ghaziabad", 28.6692m, 77.4538m),
                    ("Loni",      28.7461m, 77.2880m),
                    ("Modinagar", 28.8367m, 77.5769m),
                    ("Hapur",     28.7304m, 77.7763m),
                    ("Muradnagar",28.7817m, 77.4993m),
                }
            },
            new {
                Name = "Ghazipur", Lat = 25.5770m, Lng = 83.5753m,
                Cities = new[] {
                    ("Ghazipur",    25.5770m, 83.5753m),
                    ("Zamania",     25.4220m, 83.5540m),
                    ("Saidpur",     25.5463m, 83.6825m),
                    ("Muhammadabad",25.5300m, 83.7730m),
                }
            },
            new {
                Name = "Gonda", Lat = 27.1344m, Lng = 81.9622m,
                Cities = new[] {
                    ("Gonda",      27.1344m, 81.9622m),
                    ("Tarabganj",  27.3461m, 81.9153m),
                    ("Mankapur",   27.0488m, 82.2115m),
                    ("Colonelganj",27.1289m, 82.0952m),
                    ("Nawabganj",  27.0600m, 81.8300m),
                }
            },
            new {
                Name = "Gorakhpur", Lat = 26.7606m, Lng = 83.3732m,
                Cities = new[] {
                    ("Gorakhpur", 26.7606m, 83.3732m),
                    ("Bhathat",   26.8121m, 83.5140m),
                    ("Sahjanwa",  26.7298m, 83.4302m),
                    ("Gola",      26.4500m, 83.5800m),
                    ("Campierganj",26.8900m, 83.2800m),
                }
            },
            new {
                Name = "Hamirpur", Lat = 25.9531m, Lng = 80.1471m,
                Cities = new[] {
                    ("Hamirpur", 25.9531m, 80.1471m),
                    ("Maudaha",  25.7027m, 80.0124m),
                    ("Rath",     25.5907m, 79.5700m),
                    ("Sarila",   25.7686m, 79.6894m),
                }
            },
            new {
                Name = "Hapur", Lat = 28.7304m, Lng = 77.7763m,
                Cities = new[] {
                    ("Hapur",          28.7304m, 77.7763m),
                    ("Pilkhuwa",       28.7072m, 77.6556m),
                    ("Garh Mukteshwar",28.7848m, 78.1114m),
                    ("Dhaulana",       28.7467m, 77.7117m),
                }
            },
            new {
                Name = "Hardoi", Lat = 27.3994m, Lng = 80.1274m,
                Cities = new[] {
                    ("Hardoi",  27.3994m, 80.1274m),
                    ("Shahabad",27.6500m, 79.9300m),
                    ("Sandila", 27.0706m, 80.5192m),
                    ("Bilgram", 27.1979m, 80.0326m),
                    ("Sandi",   27.2963m, 80.2928m),
                }
            },
            new {
                Name = "Hathras", Lat = 27.5940m, Lng = 78.0533m,
                Cities = new[] {
                    ("Hathras",     27.5940m, 78.0533m),
                    ("Sadabad",     27.4405m, 78.0361m),
                    ("Sikandrarau", 27.6968m, 78.3934m),
                    ("Mursan",      27.6551m, 77.9810m),
                }
            },
            new {
                Name = "Jalaun", Lat = 25.9887m, Lng = 79.4579m,
                Cities = new[] {
                    ("Orai",   25.9887m, 79.4579m),
                    ("Konch",  26.0003m, 79.6150m),
                    ("Kalpi",  26.1176m, 79.7354m),
                    ("Jalaun", 26.1467m, 79.3374m),
                }
            },
            new {
                Name = "Jaunpur", Lat = 25.7463m, Lng = 82.6836m,
                Cities = new[] {
                    ("Jaunpur",       25.7463m, 82.6836m),
                    ("Machhalishahar",25.6723m, 82.5742m),
                    ("Mariahu",       25.5829m, 82.8671m),
                    ("Shahganj",      25.9013m, 82.6951m),
                    ("Kerakat",       25.7891m, 82.9217m),
                }
            },
            new {
                Name = "Jhansi", Lat = 25.4484m, Lng = 78.5685m,
                Cities = new[] {
                    ("Jhansi",     25.4484m, 78.5685m),
                    ("Mauranipur", 25.2559m, 79.1430m),
                    ("Moth",       25.6044m, 78.9578m),
                    ("Chirgaon",   25.5508m, 78.7609m),
                    ("Bangra",     25.3701m, 78.8094m),
                }
            },
            new {
                Name = "Kannauj", Lat = 27.0555m, Lng = 79.9176m,
                Cities = new[] {
                    ("Kannauj",   27.0555m, 79.9176m),
                    ("Chhibramau",27.1435m, 79.5153m),
                    ("Tirwa",     27.3869m, 79.8174m),
                    ("Umarda",    27.1193m, 79.7348m),
                }
            },
            new {
                Name = "Kanpur Dehat", Lat = 26.4051m, Lng = 79.7779m,
                Cities = new[] {
                    ("Akbarpur",  26.4400m, 79.8100m),
                    ("Rasulabad", 26.4051m, 79.7779m),
                    ("Bhognipur", 26.3044m, 79.8671m),
                    ("Derapur",   26.2748m, 79.7139m),
                }
            },
            new {
                Name = "Kanpur Nagar", Lat = 26.4499m, Lng = 80.3319m,
                Cities = new[] {
                    ("Kanpur",     26.4499m, 80.3319m),
                    ("Kalyanpur",  26.4879m, 80.2277m),
                    ("Ghatampur",  26.1501m, 80.1699m),
                    ("Bilhaur",    26.8658m, 79.7538m),
                    ("Shivrajpur", 26.5362m, 80.2956m),
                }
            },
            new {
                Name = "Kasganj", Lat = 27.8089m, Lng = 78.6484m,
                Cities = new[] {
                    ("Kasganj", 27.8089m, 78.6484m),
                    ("Soron",   27.8827m, 78.7507m),
                    ("Amanpur", 27.8234m, 79.0113m),
                    ("Patiyali",27.6942m, 79.0154m),
                }
            },
            new {
                Name = "Kaushambi", Lat = 25.5334m, Lng = 81.3769m,
                Cities = new[] {
                    ("Manjhanpur", 25.5334m, 81.3769m),
                    ("Sirathu",    25.6564m, 81.3149m),
                    ("Chail",      25.4893m, 81.5005m),
                    ("Sarsawan",   25.7823m, 81.4432m),
                }
            },
            new {
                Name = "Kushinagar", Lat = 26.7406m, Lng = 83.8887m,
                Cities = new[] {
                    ("Kushinagar", 26.7406m, 83.8887m),
                    ("Padrauna",   26.9008m, 83.9820m),
                    ("Tamkuhi",    26.9234m, 84.1673m),
                    ("Hata",       26.7419m, 84.1268m),
                    ("Khadda",     26.9284m, 84.0831m),
                }
            },
            new {
                Name = "Lakhimpur Kheri", Lat = 27.9462m, Lng = 80.7812m,
                Cities = new[] {
                    ("Lakhimpur",          27.9462m, 80.7812m),
                    ("Gola Gokaran Nath",  28.0789m, 80.4666m),
                    ("Dhaurahara",         28.2181m, 80.8035m),
                    ("Nighasan",           27.9618m, 80.5213m),
                }
            },
            new {
                Name = "Lalitpur", Lat = 24.6877m, Lng = 78.4127m,
                Cities = new[] {
                    ("Lalitpur",  24.6877m, 78.4127m),
                    ("Mehrauni",  24.7348m, 78.5623m),
                    ("Jakhaura",  24.7614m, 78.3874m),
                    ("Bar",       24.6091m, 78.2157m),
                }
            },
            new {
                Name = "Lucknow", Lat = 26.8467m, Lng = 80.9462m,
                Cities = new[] {
                    ("Lucknow",         26.8467m, 80.9462m),
                    ("Malihabad",       26.9216m, 80.7157m),
                    ("Bakshi Ka Talab", 26.9417m, 80.9174m),
                    ("Mohanlalganj",    26.6851m, 80.9729m),
                }
            },
            new {
                Name = "Maharajganj", Lat = 27.1307m, Lng = 83.5593m,
                Cities = new[] {
                    ("Maharajganj", 27.1307m, 83.5593m),
                    ("Nautanwa",    27.4254m, 83.4168m),
                    ("Siswa Bazar", 27.1462m, 83.6704m),
                    ("Nichlaul",    27.3271m, 83.6261m),
                }
            },
            new {
                Name = "Mahoba", Lat = 25.2902m, Lng = 79.8738m,
                Cities = new[] {
                    ("Mahoba",   25.2902m, 79.8738m),
                    ("Kulpahar", 25.3226m, 79.6345m),
                    ("Charkhari",25.4012m, 79.7442m),
                    ("Kabrai",   25.3780m, 79.9745m),
                }
            },
            new {
                Name = "Mainpuri", Lat = 27.2389m, Lng = 79.0130m,
                Cities = new[] {
                    ("Mainpuri",  27.2389m, 79.0130m),
                    ("Shikohabad",27.1074m, 78.5897m),
                    ("Karhal",    27.0349m, 79.1532m),
                    ("Bhongaon",  27.2527m, 79.1983m),
                }
            },
            new {
                Name = "Mathura", Lat = 27.4924m, Lng = 77.6737m,
                Cities = new[] {
                    ("Mathura",   27.4924m, 77.6737m),
                    ("Vrindavan", 27.5779m, 77.6964m),
                    ("Govardhan", 27.4992m, 77.4638m),
                    ("Baldeo",    27.3787m, 77.8224m),
                }
            },
            new {
                Name = "Mau", Lat = 25.9462m, Lng = 83.5573m,
                Cities = new[] {
                    ("Mau",      25.9462m, 83.5573m),
                    ("Ghosi",    26.0936m, 83.5360m),
                    ("Kopaganj", 26.0209m, 83.6484m),
                    ("Madhuban", 26.1421m, 83.6879m),
                }
            },
            new {
                Name = "Meerut", Lat = 28.9845m, Lng = 77.7064m,
                Cities = new[] {
                    ("Meerut",   28.9845m, 77.7064m),
                    ("Sardhana", 29.1460m, 77.6145m),
                    ("Mawana",   29.1041m, 77.7703m),
                    ("Hapur",    28.7304m, 77.7763m),
                    ("Modinagar",28.8367m, 77.5769m),
                }
            },
            new {
                Name = "Mirzapur", Lat = 25.1457m, Lng = 82.5689m,
                Cities = new[] {
                    ("Mirzapur", 25.1457m, 82.5689m),
                    ("Chunar",   25.1270m, 82.8750m),
                    ("Ahraura",  25.0105m, 83.0580m),
                    ("Lalganj",  25.1700m, 82.3500m),
                }
            },
            new {
                Name = "Moradabad", Lat = 28.8386m, Lng = 78.7733m,
                Cities = new[] {
                    ("Moradabad",  28.8386m, 78.7733m),
                    ("Sambhal",    28.5873m, 78.5686m),
                    ("Rampur",     28.8186m, 79.0259m),
                    ("Thakurdwara",29.0423m, 78.6147m),
                    ("Bilari",     28.6244m, 78.8323m),
                }
            },
            new {
                Name = "Muzaffarnagar", Lat = 29.4727m, Lng = 77.7085m,
                Cities = new[] {
                    ("Muzaffarnagar",29.4727m, 77.7085m),
                    ("Shamli",       29.4497m, 77.3128m),
                    ("Budhana",      29.2882m, 77.4786m),
                    ("Kairana",      29.3964m, 77.2020m),
                    ("Khatauli",     29.2835m, 77.7292m),
                }
            },
            new {
                Name = "Pilibhit", Lat = 28.6311m, Lng = 79.8047m,
                Cities = new[] {
                    ("Pilibhit",  28.6311m, 79.8047m),
                    ("Puranpur",  28.5149m, 80.1467m),
                    ("Bisalpur",  28.2974m, 79.8013m),
                    ("Barkhera",  28.5693m, 79.8829m),
                }
            },
            new {
                Name = "Pratapgarh", Lat = 25.8996m, Lng = 81.9845m,
                Cities = new[] {
                    ("Pratapgarh",  25.8996m, 81.9845m),
                    ("Kunda",       25.7191m, 81.5169m),
                    ("Rampur Kunda",25.7438m, 81.8979m),
                    ("Lalganj",     25.9200m, 81.9100m),
                }
            },
            new {
                Name = "Prayagraj", Lat = 25.4358m, Lng = 81.8463m,
                Cities = new[] {
                    ("Prayagraj", 25.4358m, 81.8463m),
                    ("Phulpur",   25.5471m, 82.0883m),
                    ("Handia",    25.3641m, 82.2356m),
                    ("Meja",      25.2361m, 81.9502m),
                    ("Bara",      25.2041m, 81.9831m),
                }
            },
            new {
                Name = "Raebareli", Lat = 26.2309m, Lng = 81.2335m,
                Cities = new[] {
                    ("Raebareli", 26.2309m, 81.2335m),
                    ("Unchahar",  26.0982m, 81.3615m),
                    ("Salon",     26.0316m, 81.4476m),
                    ("Dalmau",    26.0671m, 81.0417m),
                    ("Lalganj",   26.2400m, 81.4200m),
                }
            },
            new {
                Name = "Rampur", Lat = 28.8186m, Lng = 79.0259m,
                Cities = new[] {
                    ("Rampur", 28.8186m, 79.0259m),
                    ("Milak",  28.6513m, 79.2367m),
                    ("Swar",   28.7593m, 79.1854m),
                    ("Bilaspur",29.1900m, 79.0200m),
                }
            },
            new {
                Name = "Saharanpur", Lat = 29.9680m, Lng = 77.5510m,
                Cities = new[] {
                    ("Saharanpur",     29.9680m, 77.5510m),
                    ("Deoband",        29.6940m, 77.6790m),
                    ("Gangoh",         29.7819m, 77.2611m),
                    ("Rampur Maniharan",29.8214m, 77.4012m),
                }
            },
            new {
                Name = "Sambhal", Lat = 28.5873m, Lng = 78.5686m,
                Cities = new[] {
                    ("Sambhal",  28.5873m, 78.5686m),
                    ("Chandausi",28.4535m, 78.7768m),
                    ("Gunnaur",  28.5149m, 78.4523m),
                    ("Rajpura",  28.4918m, 78.6134m),
                }
            },
            new {
                Name = "Sant Kabir Nagar", Lat = 26.7741m, Lng = 83.0757m,
                Cities = new[] {
                    ("Khalilabad", 26.7741m, 83.0757m),
                    ("Mehdawal",   27.0015m, 83.1219m),
                    ("Baghauli",   26.9134m, 83.0048m),
                }
            },
            new {
                Name = "Shahjahanpur", Lat = 27.8840m, Lng = 79.9046m,
                Cities = new[] {
                    ("Shahjahanpur",27.8840m, 79.9046m),
                    ("Tilhar",      27.9648m, 79.7337m),
                    ("Powayan",     28.0836m, 79.9638m),
                    ("Jalalabad",   27.7284m, 79.6826m),
                }
            },
            new {
                Name = "Shamli", Lat = 29.4497m, Lng = 77.3128m,
                Cities = new[] {
                    ("Shamli",       29.4497m, 77.3128m),
                    ("Kairana",      29.3964m, 77.2020m),
                    ("Thana Bhawan", 29.5864m, 77.4005m),
                    ("Budhana",      29.2882m, 77.4786m),
                }
            },
            new {
                Name = "Shrawasti", Lat = 27.7023m, Lng = 81.9459m,
                Cities = new[] {
                    ("Bhinga",    27.7023m, 81.9459m),
                    ("Ikauna",    27.5879m, 82.1083m),
                    ("Huzoorpur", 27.8834m, 81.7869m),
                }
            },
            new {
                Name = "Siddharthnagar", Lat = 27.3097m, Lng = 82.7411m,
                Cities = new[] {
                    ("Naugarh",     27.3459m, 83.1148m),
                    ("Shohratgarh", 27.2481m, 83.1947m),
                    ("Domariyaganj",27.3097m, 82.7411m),
                    ("Banhara Ghat",27.2135m, 83.4529m),
                }
            },
            new {
                Name = "Sitapur", Lat = 27.5637m, Lng = 80.6816m,
                Cities = new[] {
                    ("Sitapur",    27.5637m, 80.6816m),
                    ("Laharpur",   27.7083m, 80.9099m),
                    ("Biswan",     27.4960m, 80.9600m),
                    ("Mahmudabad", 27.3018m, 81.1202m),
                    ("Hargaon",    27.3945m, 80.9736m),
                }
            },
            new {
                Name = "Sonbhadra", Lat = 24.6840m, Lng = 83.0683m,
                Cities = new[] {
                    ("Robertsganj", 24.6840m, 83.0683m),
                    ("Obra",        24.4535m, 82.9879m),
                    ("Renukoot",    24.2068m, 83.0423m),
                    ("Pipri",       24.5183m, 83.2347m),
                }
            },
            new {
                Name = "Sultanpur", Lat = 26.2648m, Lng = 82.0727m,
                Cities = new[] {
                    ("Sultanpur", 26.2648m, 82.0727m),
                    ("Lambua",    26.1979m, 82.1843m),
                    ("Kadipur",   26.1346m, 82.3179m),
                    ("Amethi",    26.1486m, 81.7007m),
                }
            },
            new {
                Name = "Unnao", Lat = 26.5468m, Lng = 80.4883m,
                Cities = new[] {
                    ("Unnao",      26.5468m, 80.4883m),
                    ("Purwa",      26.4548m, 80.7826m),
                    ("Bangarmau",  26.7284m, 80.2103m),
                    ("Hasanganj",  26.7133m, 80.5248m),
                    ("Safipur",    26.7349m, 80.3445m),
                }
            },
            new {
                Name = "Varanasi", Lat = 25.3176m, Lng = 82.9739m,
                Cities = new[] {
                    ("Varanasi",   25.3176m, 82.9739m),
                    ("Mughal Sarai",25.2816m, 83.1163m),
                    ("Ramnagar",   25.2777m, 83.0199m),
                    ("Pindra",     25.3957m, 83.0779m),
                    ("Cholapur",   25.2551m, 83.1864m),
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
