using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;

namespace RentNearBy.Infrastructure.Data;

public static class DataSeeder
{
    public static async Task SeedAsync(ApplicationDbContext db)
    {
        await SeedRoomTypesAsync(db);
        await SeedDistrictsAndCitiesAsync(db);
        await SeedPaymentFeatureAsync(db);
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
                    ("Shahzadpur",          30.4215m, 76.9398m),
                    ("Saha",                30.2410m, 77.0421m),
                    ("Tangri",              30.2752m, 77.1102m),
                    ("Panjokhra Sahib",     30.3385m, 76.8623m),
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
                    ("Behal",        28.6989m, 76.2487m),
                    ("Kairu",        28.7340m, 76.3200m),
                    ("Dulina",       28.6700m, 75.9800m),
                    ("Jhanjra",      28.7200m, 76.0800m),
                }
            },
            new {
                Name = "Charkhi Dadri", Lat = 28.5921m, Lng = 76.2688m,
                Cities = new[] {
                    ("Charkhi Dadri", 28.5921m, 76.2688m),
                    ("Badhra",        28.4459m, 76.0434m),
                    ("Jhojhu Kalan",  28.6614m, 76.1823m),
                    ("Bond Kalan",    28.5527m, 76.1200m),
                    ("Chhara",        28.4900m, 76.3400m),
                    ("Jui",           28.6300m, 76.3000m),
                    ("Dhanana",       28.5700m, 76.2000m),
                }
            },
            new {
                Name = "Faridabad", Lat = 28.4089m, Lng = 77.3178m,
                Cities = new[] {
                    ("Faridabad",    28.4089m, 77.3178m),
                    ("Ballabhgarh",  28.3387m, 77.3188m),
                    ("Palwal",       28.1429m, 77.3240m),
                    ("Hodal",        27.8956m, 77.3643m),
                    ("Tigaon",       28.2741m, 77.3698m),
                    ("Chhainsa",     28.1900m, 77.3400m),
                    ("Pali",         28.2400m, 77.4500m),
                    ("Prithla",      28.1500m, 77.4200m),
                }
            },
            new {
                Name = "Fatehabad", Lat = 29.5153m, Lng = 75.4549m,
                Cities = new[] {
                    ("Fatehabad",    29.5153m, 75.4549m),
                    ("Ratia",        29.6847m, 75.5721m),
                    ("Tohana",       29.6989m, 75.9055m),
                    ("Jakhal",       29.7924m, 75.8218m),
                    ("Bhattu Kalan", 29.6067m, 75.3221m),
                    ("Bhuna",        29.6200m, 75.7900m),
                    ("Sigri",        29.5600m, 75.6200m),
                    ("Nagaur",       29.4300m, 75.5800m),
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
                    ("Badshahpur",  28.3885m, 77.0463m),
                    ("Wazirabad",   28.4800m, 77.0100m),
                    ("Daultabad",   28.5100m, 76.9500m),
                    ("Bilaspur",    28.4000m, 76.9500m),
                }
            },
            new {
                Name = "Hisar", Lat = 29.1500m, Lng = 75.7228m,
                Cities = new[] {
                    ("Hisar",     29.1500m, 75.7228m),
                    ("Hansi",     29.1003m, 75.9656m),
                    ("Uklana",    29.4823m, 75.9202m),
                    ("Barwala",   29.3611m, 75.8894m),
                    ("Adampur",   29.0632m, 75.7688m),
                    ("Narnaund",  29.1961m, 76.1148m),
                    ("Agroha",    29.2800m, 75.6500m),
                    ("Balsamand", 29.1900m, 75.5800m),
                    ("Bass",      29.0500m, 76.0000m),
                }
            },
            new {
                Name = "Jhajjar", Lat = 28.6100m, Lng = 76.6548m,
                Cities = new[] {
                    ("Jhajjar",     28.6100m, 76.6548m),
                    ("Bahadurgarh", 28.6821m, 76.9357m),
                    ("Beri",        28.7115m, 76.5819m),
                    ("Machhrauli",  28.5002m, 76.7823m),
                    ("Dighal",      28.6212m, 76.5366m),
                    ("Palhawas",    28.4800m, 76.7200m),
                    ("Sahlawas",    28.6800m, 76.5000m),
                    ("Matanhail",   28.5544m, 76.4945m),
                }
            },
            new {
                Name = "Jind", Lat = 29.3161m, Lng = 76.3187m,
                Cities = new[] {
                    ("Jind",       29.3161m, 76.3187m),
                    ("Narwana",    29.6019m, 76.1106m),
                    ("Safidon",    29.4077m, 76.6732m),
                    ("Julana",     29.4273m, 76.3046m),
                    ("Uchana",     29.4793m, 76.3142m),
                    ("Pillukhera", 29.2700m, 76.4600m),
                    ("Alewa",      29.5700m, 76.1900m),
                    ("Karsindhu",  29.1600m, 76.3200m),
                    ("Pundi Kalan",29.2400m, 76.2400m),
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
                    ("Siwan",   29.8600m, 76.6600m),
                    ("Rajound", 29.8800m, 76.2300m),
                    ("Dhand",   29.7700m, 76.4700m),
                    ("Rawal",   29.9000m, 76.2800m),
                }
            },
            new {
                Name = "Karnal", Lat = 29.6857m, Lng = 76.9905m,
                Cities = new[] {
                    ("Karnal",    29.6857m, 76.9905m),
                    ("Assandh",   29.5010m, 76.5693m),
                    ("Gharaunda", 29.5420m, 76.9740m),
                    ("Nilokheri", 29.8445m, 76.9437m),
                    ("Indri",     29.8477m, 77.1726m),
                    ("Taraori",   29.7500m, 76.9200m),
                    ("Nissing",   29.6200m, 76.8200m),
                    ("Kunjpura",  29.6800m, 77.0600m),
                    ("Issar",     29.7100m, 77.0200m),
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
                    ("Pipli",       30.0166m, 76.8389m),
                    ("Babain",      29.9400m, 77.0600m),
                    ("Ismailabad",  30.0800m, 76.7200m),
                    ("Sirsala",     29.8800m, 76.9500m),
                }
            },
            new {
                Name = "Mahendragarh", Lat = 28.0479m, Lng = 76.1085m,
                Cities = new[] {
                    ("Narnaul",          28.0479m, 76.1085m),
                    ("Mahendragarh",     28.2755m, 76.1490m),
                    ("Ateli",            28.1197m, 76.0474m),
                    ("Nangal Chaudhary", 28.0540m, 76.2353m),
                    ("Kanina",           28.1600m, 76.0000m),
                    ("Satnali",          28.1900m, 76.2800m),
                    ("Nizampur",         28.0700m, 76.0400m),
                    ("Siana",            28.1100m, 75.9900m),
                }
            },
            new {
                Name = "Nuh", Lat = 28.0954m, Lng = 77.0007m,
                Cities = new[] {
                    ("Nuh",              28.0954m, 77.0007m),
                    ("Punhana",          28.1316m, 77.1155m),
                    ("Ferozepur Jhirka", 27.9886m, 77.0450m),
                    ("Tauru",            28.2303m, 77.0070m),
                    ("Nagina",           28.1000m, 77.2000m),
                    ("Pinangwan",        27.9300m, 77.1200m),
                    ("Shikrawa",         28.0500m, 77.0500m),
                    ("Adbar",            28.1700m, 77.0600m),
                }
            },
            new {
                Name = "Palwal", Lat = 28.1429m, Lng = 77.3240m,
                Cities = new[] {
                    ("Palwal",      28.1429m, 77.3240m),
                    ("Hodal",       27.8956m, 77.3643m),
                    ("Hathin",      27.9937m, 77.2626m),
                    ("Hassanpur",   28.0643m, 77.2791m),
                    ("Aurangabad",  28.0982m, 77.1765m),
                    ("Prithla",     28.1500m, 77.4200m),
                    ("Palhawas",    28.0400m, 77.3400m),
                    ("Asawati",     28.0200m, 77.2900m),
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
                    ("Barwala",     30.6300m, 76.7800m),
                    ("Chandimandir",30.7200m, 76.8300m),
                    ("Ramgarh",     30.5900m, 77.0000m),
                }
            },
            new {
                Name = "Panipat", Lat = 29.3909m, Lng = 76.9635m,
                Cities = new[] {
                    ("Panipat",  29.3909m, 76.9635m),
                    ("Samalkha", 29.2381m, 77.0138m),
                    ("Israna",   29.4622m, 76.8527m),
                    ("Madlauda", 29.4774m, 77.0613m),
                    ("Bapoli",   29.3700m, 77.1100m),
                    ("Sanoli",   29.2700m, 76.8800m),
                    ("Jaurasi",  29.4200m, 76.8000m),
                    ("Naultha",  29.4600m, 77.0400m),
                }
            },
            new {
                Name = "Rewari", Lat = 28.1974m, Lng = 76.6187m,
                Cities = new[] {
                    ("Rewari",    28.1974m, 76.6187m),
                    ("Bawal",     28.0706m, 76.5787m),
                    ("Dharuhera", 28.2046m, 76.7927m),
                    ("Kosli",     28.4007m, 76.5249m),
                    ("Jatusana",  28.0700m, 76.4700m),
                    ("Nahar",     28.3300m, 76.6100m),
                    ("Khol",      28.1500m, 76.6000m),
                    ("Palhawas",  28.2600m, 76.4800m),
                }
            },
            new {
                Name = "Rohtak", Lat = 28.8955m, Lng = 76.6066m,
                Cities = new[] {
                    ("Rohtak",       28.8955m, 76.6066m),
                    ("Asthal Bohar", 28.8407m, 76.7012m),
                    ("Lakhan Majra", 28.9655m, 76.5148m),
                    ("Sampla",       28.8327m, 76.8281m),
                    ("Maham",        28.9667m, 76.3165m),
                    ("Kiloi",        28.9200m, 76.4500m),
                    ("Kalanaur",     29.0600m, 76.4400m),
                    ("Mokhra",       28.8300m, 76.4400m),
                }
            },
            new {
                Name = "Sirsa", Lat = 29.5328m, Lng = 75.0313m,
                Cities = new[] {
                    ("Sirsa",            29.5328m, 75.0313m),
                    ("Dabwali",          29.9768m, 74.7258m),
                    ("Ellenabad",        29.4520m, 74.6607m),
                    ("Rania",            29.5398m, 74.8396m),
                    ("Kalanwali",        29.8100m, 74.9700m),
                    ("Nathusari Chopta", 29.5600m, 75.1500m),
                    ("Odhan",            29.6700m, 75.2300m),
                    ("Sukhchain",        29.5200m, 74.7800m),
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
                    ("Murthal",  29.0800m, 77.1000m),
                    ("Mundlana", 29.1300m, 76.8700m),
                    ("Kathura",  28.8900m, 76.8600m),
                    ("Gannaur",  29.0400m, 76.9500m),
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
                    ("Mustafabad",   30.1600m, 77.4900m),
                    ("Sadhaura",     30.3800m, 77.3400m),
                    ("Buria",        30.2200m, 77.1800m),
                    ("Khizrabad",    30.0900m, 77.2600m),
                }
            },

            // ── UTTAR PRADESH ────────────────────────────────────────────────────────
            new {
                Name = "Agra", Lat = 27.1767m, Lng = 78.0081m,
                Cities = new[] {
                    ("Agra",           27.1767m, 78.0081m),
                    ("Firozabad",      27.1522m, 78.3953m),
                    ("Fatehabad",      27.1116m, 78.2370m),
                    ("Kheragarh",      27.0561m, 78.5536m),
                    ("Bah",            26.8810m, 78.5930m),
                    ("Etmadpur",       27.2461m, 78.2399m),
                    ("Fatehpur Sikri", 27.0949m, 77.6592m),
                    ("Kiraoli",        27.0800m, 78.3200m),
                    ("Shamsabad",      27.0800m, 78.1000m),
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
                    ("Tappal",  27.8900m, 77.7000m),
                    ("Gabhana", 28.0900m, 78.0900m),
                    ("Beswan",  28.0200m, 78.1600m),
                    ("Akrabad", 27.7700m, 78.3400m),
                }
            },
            new {
                Name = "Ambedkar Nagar", Lat = 26.4315m, Lng = 82.5360m,
                Cities = new[] {
                    ("Akbarpur",    26.4315m, 82.5360m),
                    ("Tanda",       26.5613m, 82.5927m),
                    ("Jalalpur",    26.3274m, 82.7077m),
                    ("Allapur",     26.3912m, 82.4509m),
                    ("Bhiti",       26.4800m, 82.6400m),
                    ("Katehri",     26.5200m, 82.4500m),
                    ("Jahangirganj",26.4000m, 82.6700m),
                    ("Shahpur",     26.3600m, 82.4900m),
                }
            },
            new {
                Name = "Amethi", Lat = 26.1486m, Lng = 81.7007m,
                Cities = new[] {
                    ("Amethi",       26.1486m, 81.7007m),
                    ("Gauriganj",    26.1967m, 81.8194m),
                    ("Musafirkhana", 26.3570m, 81.8006m),
                    ("Salon",        26.0316m, 81.4476m),
                    ("Jagdishpur",   26.0200m, 81.6900m),
                    ("Bazar Shukul", 26.2700m, 81.8900m),
                    ("Sinhawal",     26.0700m, 81.6400m),
                    ("Tikar",        26.2100m, 81.6200m),
                }
            },
            new {
                Name = "Amroha", Lat = 28.9045m, Lng = 78.4676m,
                Cities = new[] {
                    ("Amroha",         28.9045m, 78.4676m),
                    ("Hasanpur",       28.7222m, 78.2792m),
                    ("Dhanaura",       28.9514m, 78.2416m),
                    ("Gajraula",       28.8295m, 78.2786m),
                    ("Naugawan Sadat", 28.9300m, 78.6000m),
                    ("Joya",           28.8500m, 78.5500m),
                    ("Tanda",          28.9700m, 78.9200m),
                    ("Bachhraon",      29.1300m, 78.2100m),
                }
            },
            new {
                Name = "Auraiya", Lat = 26.4673m, Lng = 79.5098m,
                Cities = new[] {
                    ("Auraiya",   26.4673m, 79.5098m),
                    ("Dibiyapur", 26.5694m, 79.5295m),
                    ("Bidhuna",   26.8016m, 79.5063m),
                    ("Ajitmal",   26.6165m, 79.3530m),
                    ("Phaphund",  26.6100m, 79.4600m),
                    ("Chakarnagar",26.3600m, 79.3800m),
                    ("Saurikh",   26.5700m, 79.6500m),
                    ("Bhagyanagar",26.7100m, 79.5400m),
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
                    ("Tanda",    26.5600m, 82.5800m),
                    ("Rudauli",  26.7200m, 81.8900m),
                    ("Maya Bazar",26.8200m, 82.0500m),
                    ("Tarun",    26.7000m, 82.2500m),
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
                    ("Atraulia", 26.2200m, 83.0500m),
                    ("Mehnagar", 26.1900m, 83.2800m),
                    ("Jiyanpur", 26.0100m, 83.3200m),
                    ("Tarwa",    26.0500m, 83.2300m),
                }
            },
            new {
                Name = "Baghpat", Lat = 28.9484m, Lng = 77.2165m,
                Cities = new[] {
                    ("Baghpat",   28.9484m, 77.2165m),
                    ("Baraut",    29.0988m, 77.2645m),
                    ("Khekra",    28.8612m, 77.2773m),
                    ("Pilana",    29.0025m, 77.1469m),
                    ("Chhaprauli",29.0900m, 77.1700m),
                    ("Siyana",    28.8800m, 77.3800m),
                    ("Doghat",    28.9500m, 77.3200m),
                    ("Patla",     28.9800m, 77.2800m),
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
                    ("Payagpur",  27.8400m, 81.7800m),
                    ("Mihinpurwa",27.9800m, 81.6900m),
                    ("Balha",     27.6100m, 81.7100m),
                    ("Tejwapur",  27.7300m, 81.4700m),
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
                    ("Narhi",       26.0400m, 84.1900m),
                    ("Reoti",       25.9600m, 84.0000m),
                    ("Hanumanganj", 25.8200m, 84.0900m),
                    ("Maniyar",     25.6900m, 84.2500m),
                }
            },
            new {
                Name = "Balrampur", Lat = 27.4310m, Lng = 82.1826m,
                Cities = new[] {
                    ("Balrampur", 27.4310m, 82.1826m),
                    ("Tulsipur",  27.5311m, 82.4076m),
                    ("Utraula",   27.3236m, 82.4192m),
                    ("Gaisdi",    27.4648m, 82.1253m),
                    ("Gainsari",  27.2700m, 82.3600m),
                    ("Rehra Bazar",27.5800m, 82.2900m),
                    ("Pachperwa", 27.4500m, 82.3300m),
                    ("Shravasti", 27.4800m, 81.9600m),
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
                    ("Mataundh", 25.3400m, 80.2100m),
                    ("Pailani",  25.0900m, 80.4600m),
                    ("Bisanda",  25.4200m, 80.6200m),
                    ("Kamasin",  25.3900m, 80.5400m),
                }
            },
            new {
                Name = "Barabanki", Lat = 26.9277m, Lng = 81.1848m,
                Cities = new[] {
                    ("Barabanki",    26.9277m, 81.1848m),
                    ("Haidergarh",   26.8048m, 81.4388m),
                    ("Ram Nagar",    26.9157m, 81.3501m),
                    ("Nawabganj",    26.9323m, 81.2471m),
                    ("Ramsanehihat", 26.8900m, 81.5200m),
                    ("Fatehpur",     26.8800m, 81.3700m),
                    ("Dewa",         26.7500m, 81.0200m),
                    ("Sirauli",      26.9800m, 81.3900m),
                    ("Masauli",      26.9000m, 81.5700m),
                }
            },
            new {
                Name = "Bareilly", Lat = 28.3670m, Lng = 79.4304m,
                Cities = new[] {
                    ("Bareilly",   28.3670m, 79.4304m),
                    ("Pilibhit",   28.6311m, 79.8047m),
                    ("Baheri",     28.7586m, 79.4912m),
                    ("Aonla",      28.3985m, 79.3862m),
                    ("Fatehganj",  28.4543m, 79.3501m),
                    ("Mirganj",    28.2900m, 79.3100m),
                    ("Nawabganj",  28.5300m, 79.6800m),
                    ("Faridpur",   28.1600m, 79.5700m),
                    ("Shergarh",   28.0800m, 79.5200m),
                }
            },
            new {
                Name = "Basti", Lat = 26.8009m, Lng = 82.7289m,
                Cities = new[] {
                    ("Basti",      26.8009m, 82.7289m),
                    ("Harraiya",   26.9058m, 82.9677m),
                    ("Kaptanganj", 26.9119m, 83.0564m),
                    ("Bhanpur",    26.8540m, 82.6978m),
                    ("Rudhauli",   26.9700m, 82.8600m),
                    ("Nawabganj",  26.7200m, 82.6200m),
                    ("Bansi",      27.1800m, 83.0600m),
                    ("Bakhira",    26.9400m, 83.1100m),
                }
            },
            new {
                Name = "Bhadohi", Lat = 25.3926m, Lng = 82.5672m,
                Cities = new[] {
                    ("Bhadohi",   25.3926m, 82.5672m),
                    ("Gyanpur",   25.3517m, 82.5946m),
                    ("Aurai",     25.3084m, 82.7152m),
                    ("Suriyawan", 25.3474m, 82.6427m),
                    ("Gopiganj",  25.4600m, 82.4700m),
                    ("Abholi",    25.3200m, 82.6100m),
                    ("Dighwara",  25.4100m, 82.5200m),
                    ("Khamaria",  25.3700m, 82.6900m),
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
                    ("Dhampur",   29.3100m, 78.5100m),
                    ("Seohara",   29.2200m, 78.5700m),
                    ("Noorpur",   29.1500m, 78.6500m),
                    ("Haldaur",   29.4100m, 78.1100m),
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
                    ("Shedpur",  28.1600m, 79.0400m),
                    ("Wazirganj",27.7500m, 79.0900m),
                    ("Islamnagar",28.0500m, 79.0600m),
                    ("Asafpur",  28.2500m, 79.2200m),
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
                    ("Dibai",       28.2200m, 78.2300m),
                    ("Jahangirabad",28.5700m, 78.0800m),
                    ("Gulaothi",    28.4400m, 77.9700m),
                    ("Danpur",      28.3200m, 78.0200m),
                }
            },
            new {
                Name = "Chandauli", Lat = 25.2706m, Lng = 83.2693m,
                Cities = new[] {
                    ("Chandauli",   25.2706m, 83.2693m),
                    ("Mughal Sarai",25.2816m, 83.1163m),
                    ("Sakaldiha",   25.3671m, 83.0759m),
                    ("Chahaniya",   25.1953m, 83.3826m),
                    ("Naugarh",     25.2000m, 83.5200m),
                    ("Chakia",      25.0500m, 83.1800m),
                    ("Barhani",     25.1600m, 83.4500m),
                    ("Dhanapur",    25.2800m, 83.0400m),
                }
            },
            new {
                Name = "Chitrakoot", Lat = 25.1994m, Lng = 80.8978m,
                Cities = new[] {
                    ("Chitrakoot", 25.1994m, 80.8978m),
                    ("Karwi",      25.2025m, 80.9011m),
                    ("Manikpur",   25.0631m, 81.1024m),
                    ("Mau",        25.2800m, 80.8500m),
                    ("Rajapur",    25.0600m, 80.7500m),
                    ("Pahadi",     25.3400m, 80.9700m),
                    ("Ramnagar",   25.1300m, 80.7900m),
                    ("Bargarh",    25.1000m, 80.9500m),
                }
            },
            new {
                Name = "Deoria", Lat = 26.5044m, Lng = 83.7840m,
                Cities = new[] {
                    ("Deoria",        26.5044m, 83.7840m),
                    ("Bhatpar Rani",  26.3023m, 83.9749m),
                    ("Salempur",      26.3012m, 83.8754m),
                    ("Barhaj",        26.2643m, 83.8098m),
                    ("Rudrapur",      26.3800m, 83.9600m),
                    ("Gauri Bazar",   26.6800m, 83.6700m),
                    ("Rampur Karkhana",26.3500m, 84.0900m),
                    ("Tarwa",         26.5200m, 83.9200m),
                    ("Lar",           26.2400m, 83.7500m),
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
                    ("Awankhurd",27.7800m, 78.7400m),
                    ("Marehra", 27.6800m, 79.2800m),
                    ("Nigoh",   27.5200m, 78.8900m),
                    ("Sakeet",  27.4200m, 78.9700m),
                }
            },
            new {
                Name = "Etawah", Lat = 26.7828m, Lng = 79.0247m,
                Cities = new[] {
                    ("Etawah",       26.7828m, 79.0247m),
                    ("Jaswantnagar", 26.8935m, 79.2316m),
                    ("Bharthana",    26.7399m, 79.3052m),
                    ("Saifai",       26.9887m, 79.2843m),
                    ("Chakarnagar",  26.3600m, 79.3800m),
                    ("Ekdil",        26.6200m, 79.1200m),
                    ("Kadaura",      26.8400m, 79.3200m),
                    ("Lakhaura",     26.7200m, 79.1500m),
                }
            },
            new {
                Name = "Farrukhabad", Lat = 27.3904m, Lng = 79.5799m,
                Cities = new[] {
                    ("Farrukhabad", 27.3904m, 79.5799m),
                    ("Fatehgarh",   27.3661m, 79.6326m),
                    ("Kaimganj",    27.5612m, 79.3456m),
                    ("Shamsabad",   27.3456m, 79.7113m),
                    ("Rajpur",      27.4800m, 79.7600m),
                    ("Mohammadabad",27.2800m, 79.4600m),
                    ("Kamalganj",   27.5400m, 79.5900m),
                    ("Amritpur",    27.5900m, 79.6700m),
                }
            },
            new {
                Name = "Fatehpur", Lat = 25.9280m, Lng = 80.8124m,
                Cities = new[] {
                    ("Fatehpur",    25.9280m, 80.8124m),
                    ("Bindki",      25.9822m, 80.5799m),
                    ("Khaga",       25.7749m, 81.0290m),
                    ("Hussainganj", 25.9010m, 80.8952m),
                    ("Malwan",      25.7200m, 80.9200m),
                    ("Hathgaon",    26.0600m, 80.7800m),
                    ("Asothar",     25.8700m, 80.9600m),
                    ("Teliyani",    26.0100m, 80.8200m),
                }
            },
            new {
                Name = "Firozabad", Lat = 27.1522m, Lng = 78.3953m,
                Cities = new[] {
                    ("Firozabad",  27.1522m, 78.3953m),
                    ("Shikohabad", 27.1074m, 78.5897m),
                    ("Tundla",     27.2104m, 78.2427m),
                    ("Jasrana",    27.2447m, 78.6530m),
                    ("Sirsaganj",  27.0600m, 78.6900m),
                    ("Narkhi",     27.1800m, 78.4800m),
                    ("Madanpur",   27.3000m, 78.5600m),
                    ("Araon",      27.2700m, 78.4200m),
                }
            },
            new {
                Name = "Gautam Buddha Nagar", Lat = 28.5355m, Lng = 77.3910m,
                Cities = new[] {
                    ("Noida",         28.5355m, 77.3910m),
                    ("Greater Noida", 28.4744m, 77.5040m),
                    ("Dadri",         28.5557m, 77.5533m),
                    ("Jewar",         28.1237m, 77.5547m),
                    ("Dankaur",       28.4400m, 77.5500m),
                    ("Bisrakh",       28.4900m, 77.4300m),
                    ("Rabupura",      28.3700m, 77.5900m),
                    ("Jarcha",        28.4700m, 77.4800m),
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
                    ("Dasna",     28.7000m, 77.5300m),
                    ("Tronica City",28.7200m, 77.4000m),
                    ("Sikri",     28.7300m, 77.6200m),
                    ("Khoda",     28.6500m, 77.4200m),
                }
            },
            new {
                Name = "Ghazipur", Lat = 25.5770m, Lng = 83.5753m,
                Cities = new[] {
                    ("Ghazipur",     25.5770m, 83.5753m),
                    ("Zamania",      25.4220m, 83.5540m),
                    ("Saidpur",      25.5463m, 83.6825m),
                    ("Muhammadabad", 25.5300m, 83.7730m),
                    ("Jakhania",     25.4600m, 83.7200m),
                    ("Jangipur",     25.5100m, 83.7000m),
                    ("Yusufpur",     25.6600m, 83.6200m),
                    ("Karanda",      25.4000m, 83.4800m),
                }
            },
            new {
                Name = "Gonda", Lat = 27.1344m, Lng = 81.9622m,
                Cities = new[] {
                    ("Gonda",       27.1344m, 81.9622m),
                    ("Tarabganj",   27.3461m, 81.9153m),
                    ("Mankapur",    27.0488m, 82.2115m),
                    ("Colonelganj", 27.1289m, 82.0952m),
                    ("Nawabganj",   27.0600m, 81.8300m),
                    ("Karna",       27.1000m, 82.0500m),
                    ("Khajura",     27.2800m, 81.8800m),
                    ("Itiyathok",   27.2100m, 82.2500m),
                    ("Belsar",      27.3200m, 81.9700m),
                }
            },
            new {
                Name = "Gorakhpur", Lat = 26.7606m, Lng = 83.3732m,
                Cities = new[] {
                    ("Gorakhpur",  26.7606m, 83.3732m),
                    ("Bhathat",    26.8121m, 83.5140m),
                    ("Sahjanwa",   26.7298m, 83.4302m),
                    ("Gola",       26.4500m, 83.5800m),
                    ("Campierganj",26.8900m, 83.2800m),
                    ("Bansgaon",   26.5500m, 83.5100m),
                    ("Barhalganj", 26.6000m, 83.5800m),
                    ("Pipraich",   26.8100m, 83.5000m),
                    ("Chauri Chaura",26.8200m, 83.1700m),
                }
            },
            new {
                Name = "Hamirpur", Lat = 25.9531m, Lng = 80.1471m,
                Cities = new[] {
                    ("Hamirpur", 25.9531m, 80.1471m),
                    ("Maudaha",  25.7027m, 80.0124m),
                    ("Rath",     25.5907m, 79.5700m),
                    ("Sarila",   25.7686m, 79.6894m),
                    ("Kurara",   25.8200m, 80.0400m),
                    ("Sumerpur", 25.9800m, 79.6300m),
                    ("Mustara",  25.7500m, 80.2100m),
                    ("Gohand",   25.7000m, 79.9700m),
                }
            },
            new {
                Name = "Hapur", Lat = 28.7304m, Lng = 77.7763m,
                Cities = new[] {
                    ("Hapur",           28.7304m, 77.7763m),
                    ("Pilkhuwa",        28.7072m, 77.6556m),
                    ("Garh Mukteshwar", 28.7848m, 78.1114m),
                    ("Dhaulana",        28.7467m, 77.7117m),
                    ("Simbhaoli",       28.7000m, 77.7900m),
                    ("Pahsu",           28.6000m, 77.6700m),
                    ("Asafpur",         28.7700m, 77.8700m),
                    ("Dobhi",           28.8100m, 77.9200m),
                }
            },
            new {
                Name = "Hardoi", Lat = 27.3994m, Lng = 80.1274m,
                Cities = new[] {
                    ("Hardoi",     27.3994m, 80.1274m),
                    ("Shahabad",   27.6500m, 79.9300m),
                    ("Sandila",    27.0706m, 80.5192m),
                    ("Bilgram",    27.1979m, 80.0326m),
                    ("Sandi",      27.2963m, 80.2928m),
                    ("Bawan",      27.4800m, 80.3700m),
                    ("Sawayajpur", 27.2100m, 80.1900m),
                    ("Mallanwan",  27.1800m, 80.3000m),
                    ("Madhoganj",  27.5100m, 80.3900m),
                }
            },
            new {
                Name = "Hathras", Lat = 27.5940m, Lng = 78.0533m,
                Cities = new[] {
                    ("Hathras",     27.5940m, 78.0533m),
                    ("Sadabad",     27.4405m, 78.0361m),
                    ("Sikandrarau", 27.6968m, 78.3934m),
                    ("Mursan",      27.6551m, 77.9810m),
                    ("Sasni",       27.7000m, 78.0200m),
                    ("Mendu",       27.6000m, 78.1600m),
                    ("Hasayan",     27.4900m, 78.1900m),
                    ("Kwarsi",      27.7500m, 78.3200m),
                }
            },
            new {
                Name = "Jalaun", Lat = 25.9887m, Lng = 79.4579m,
                Cities = new[] {
                    ("Orai",      25.9887m, 79.4579m),
                    ("Konch",     26.0003m, 79.6150m),
                    ("Kalpi",     26.1176m, 79.7354m),
                    ("Jalaun",    26.1467m, 79.3374m),
                    ("Ramnagar",  26.0200m, 79.2300m),
                    ("Madhogarh", 26.0700m, 79.3000m),
                    ("Kona",      25.9000m, 79.4000m),
                    ("Atarrha",   25.8500m, 79.5000m),
                }
            },
            new {
                Name = "Jaunpur", Lat = 25.7463m, Lng = 82.6836m,
                Cities = new[] {
                    ("Jaunpur",        25.7463m, 82.6836m),
                    ("Machhalishahar", 25.6723m, 82.5742m),
                    ("Mariahu",        25.5829m, 82.8671m),
                    ("Shahganj",       25.9013m, 82.6951m),
                    ("Kerakat",        25.7891m, 82.9217m),
                    ("Badlapur",       25.5900m, 82.7700m),
                    ("Sujanganj",      25.6900m, 82.9200m),
                    ("Muftiganj",      25.8800m, 82.4900m),
                    ("Dobhi",          25.5200m, 82.7100m),
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
                    ("Mau Ranipur",25.1800m, 79.0800m),
                    ("Samthar",    25.2500m, 78.9400m),
                    ("Garautha",   25.5000m, 79.2400m),
                    ("Talbehat",   25.0200m, 78.8700m),
                }
            },
            new {
                Name = "Kannauj", Lat = 27.0555m, Lng = 79.9176m,
                Cities = new[] {
                    ("Kannauj",    27.0555m, 79.9176m),
                    ("Chhibramau", 27.1435m, 79.5153m),
                    ("Tirwa",      27.3869m, 79.8174m),
                    ("Umarda",     27.1193m, 79.7348m),
                    ("Samapur",    27.1600m, 79.8300m),
                    ("Gursahaiganj",27.0800m, 79.7900m),
                    ("Talgram",    27.2400m, 79.7600m),
                    ("Saurikh",    27.0000m, 79.8500m),
                }
            },
            new {
                Name = "Kanpur Dehat", Lat = 26.4051m, Lng = 79.7779m,
                Cities = new[] {
                    ("Akbarpur",  26.4400m, 79.8100m),
                    ("Rasulabad", 26.4051m, 79.7779m),
                    ("Bhognipur", 26.3044m, 79.8671m),
                    ("Derapur",   26.2748m, 79.7139m),
                    ("Pukhrayan", 26.2300m, 79.8400m),
                    ("Rura",      26.4000m, 79.5800m),
                    ("Maitha",    26.3000m, 79.8900m),
                    ("Sarsaul",   26.5200m, 80.0600m),
                }
            },
            new {
                Name = "Kanpur Nagar", Lat = 26.4499m, Lng = 80.3319m,
                Cities = new[] {
                    ("Kanpur",      26.4499m, 80.3319m),
                    ("Kalyanpur",   26.4879m, 80.2277m),
                    ("Ghatampur",   26.1501m, 80.1699m),
                    ("Bilhaur",     26.8658m, 79.7538m),
                    ("Shivrajpur",  26.5362m, 80.2956m),
                    ("Armapur",     26.5000m, 80.3700m),
                    ("Nawabganj",   26.6100m, 80.4100m),
                    ("Chaubeypur",  26.5700m, 80.1200m),
                    ("Panki",       26.4500m, 80.2800m),
                }
            },
            new {
                Name = "Kasganj", Lat = 27.8089m, Lng = 78.6484m,
                Cities = new[] {
                    ("Kasganj",       27.8089m, 78.6484m),
                    ("Soron",         27.8827m, 78.7507m),
                    ("Amanpur",       27.8234m, 79.0113m),
                    ("Patiyali",      27.6942m, 79.0154m),
                    ("Ganjdundwara",  27.6700m, 79.0000m),
                    ("Sahawar",       27.8000m, 79.1300m),
                    ("Shandara",      27.9200m, 78.8500m),
                    ("Sidhpura",      27.7500m, 79.1500m),
                }
            },
            new {
                Name = "Kaushambi", Lat = 25.5334m, Lng = 81.3769m,
                Cities = new[] {
                    ("Manjhanpur", 25.5334m, 81.3769m),
                    ("Sirathu",    25.6564m, 81.3149m),
                    ("Chail",      25.4893m, 81.5005m),
                    ("Sarsawan",   25.7823m, 81.4432m),
                    ("Bharwari",   25.4600m, 81.4400m),
                    ("Sarayaksha", 25.7100m, 81.3200m),
                    ("Purwa",      25.5000m, 81.3000m),
                    ("Chahniya",   25.4200m, 81.5500m),
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
                    ("Ramkola",    26.6700m, 83.8700m),
                    ("Seorahi",    26.5800m, 84.0100m),
                    ("Kasaya",     26.6200m, 83.7500m),
                    ("Sukulpur",   26.8700m, 83.8200m),
                }
            },
            new {
                Name = "Lakhimpur Kheri", Lat = 27.9462m, Lng = 80.7812m,
                Cities = new[] {
                    ("Lakhimpur",         27.9462m, 80.7812m),
                    ("Gola Gokaran Nath", 28.0789m, 80.4666m),
                    ("Dhaurahara",        28.2181m, 80.8035m),
                    ("Nighasan",          27.9618m, 80.5213m),
                    ("Mohammadi",         28.0700m, 80.6200m),
                    ("Pallia Kalan",      28.3700m, 80.5800m),
                    ("Phoolbehar",        27.8300m, 80.8800m),
                    ("Nakaha",            28.0800m, 80.9500m),
                }
            },
            new {
                Name = "Lalitpur", Lat = 24.6877m, Lng = 78.4127m,
                Cities = new[] {
                    ("Lalitpur",  24.6877m, 78.4127m),
                    ("Mehrauni",  24.7348m, 78.5623m),
                    ("Jakhaura",  24.7614m, 78.3874m),
                    ("Bar",       24.6091m, 78.2157m),
                    ("Talbehat",  25.0200m, 78.8700m),
                    ("Madawara",  24.8500m, 78.6300m),
                    ("Pali",      24.5900m, 78.1500m),
                    ("Bihariganj",24.7800m, 78.5100m),
                }
            },
            new {
                Name = "Lucknow", Lat = 26.8467m, Lng = 80.9462m,
                Cities = new[] {
                    ("Lucknow",          26.8467m, 80.9462m),
                    ("Malihabad",        26.9216m, 80.7157m),
                    ("Bakshi Ka Talab",  26.9417m, 80.9174m),
                    ("Mohanlalganj",     26.6851m, 80.9729m),
                    ("Kakori",           26.9000m, 80.7800m),
                    ("Gosainganj",       26.6300m, 81.0400m),
                    ("Itaunja",          26.9700m, 80.5800m),
                    ("Chinhat",          26.8800m, 81.0600m),
                    ("Mal",              26.7500m, 80.8500m),
                }
            },
            new {
                Name = "Maharajganj", Lat = 27.1307m, Lng = 83.5593m,
                Cities = new[] {
                    ("Maharajganj", 27.1307m, 83.5593m),
                    ("Nautanwa",    27.4254m, 83.4168m),
                    ("Siswa Bazar", 27.1462m, 83.6704m),
                    ("Nichlaul",    27.3271m, 83.6261m),
                    ("Anandnagar",  27.2900m, 83.5500m),
                    ("Ghughli",     27.0900m, 83.7200m),
                    ("Farenda",     27.2100m, 83.3500m),
                    ("Partawal",    27.3800m, 83.7800m),
                }
            },
            new {
                Name = "Mahoba", Lat = 25.2902m, Lng = 79.8738m,
                Cities = new[] {
                    ("Mahoba",   25.2902m, 79.8738m),
                    ("Kulpahar", 25.3226m, 79.6345m),
                    ("Charkhari",25.4012m, 79.7442m),
                    ("Kabrai",   25.3780m, 79.9745m),
                    ("Panwari",  25.3700m, 79.6700m),
                    ("Srinagar", 25.2400m, 79.8300m),
                    ("Jaitpur",  25.1800m, 79.9200m),
                    ("Khanna",   25.3100m, 79.7600m),
                }
            },
            new {
                Name = "Mainpuri", Lat = 27.2389m, Lng = 79.0130m,
                Cities = new[] {
                    ("Mainpuri",  27.2389m, 79.0130m),
                    ("Shikohabad",27.1074m, 78.5897m),
                    ("Karhal",    27.0349m, 79.1532m),
                    ("Bhongaon",  27.2527m, 79.1983m),
                    ("Kishni",    27.1500m, 79.0100m),
                    ("Kuraoli",   27.1200m, 79.2700m),
                    ("Ghiror",    27.0400m, 79.0800m),
                    ("Bewar",     27.3700m, 79.2500m),
                }
            },
            new {
                Name = "Mathura", Lat = 27.4924m, Lng = 77.6737m,
                Cities = new[] {
                    ("Mathura",   27.4924m, 77.6737m),
                    ("Vrindavan", 27.5779m, 77.6964m),
                    ("Govardhan", 27.4992m, 77.4638m),
                    ("Baldeo",    27.3787m, 77.8224m),
                    ("Mahaban",   27.4200m, 77.6100m),
                    ("Chhata",    27.7300m, 77.5000m),
                    ("Mant",      27.3300m, 77.7500m),
                    ("Kosi Kalan",27.7900m, 77.4400m),
                }
            },
            new {
                Name = "Mau", Lat = 25.9462m, Lng = 83.5573m,
                Cities = new[] {
                    ("Mau",        25.9462m, 83.5573m),
                    ("Ghosi",      26.0936m, 83.5360m),
                    ("Kopaganj",   26.0209m, 83.6484m),
                    ("Madhuban",   26.1421m, 83.6879m),
                    ("Ratanpura",  26.0500m, 83.4800m),
                    ("Dohrighat",  26.0300m, 83.8200m),
                    ("Mohanpur",   25.9800m, 83.6400m),
                    ("Phephna",    26.0200m, 83.5500m),
                }
            },
            new {
                Name = "Meerut", Lat = 28.9845m, Lng = 77.7064m,
                Cities = new[] {
                    ("Meerut",      28.9845m, 77.7064m),
                    ("Sardhana",    29.1460m, 77.6145m),
                    ("Mawana",      29.1041m, 77.7703m),
                    ("Hapur",       28.7304m, 77.7763m),
                    ("Modinagar",   28.8367m, 77.5769m),
                    ("Kithore",     28.7900m, 77.8100m),
                    ("Garh",        29.1100m, 78.1700m),
                    ("Parikshitgarh",29.0100m, 77.9100m),
                    ("Daurala",     29.0200m, 77.7200m),
                }
            },
            new {
                Name = "Mirzapur", Lat = 25.1457m, Lng = 82.5689m,
                Cities = new[] {
                    ("Mirzapur", 25.1457m, 82.5689m),
                    ("Chunar",   25.1270m, 82.8750m),
                    ("Ahraura",  25.0105m, 83.0580m),
                    ("Lalganj",  25.1700m, 82.3500m),
                    ("Marihan",  24.9200m, 82.9500m),
                    ("Hallia",   25.0800m, 82.5100m),
                    ("Majhwan",  25.3000m, 82.8500m),
                    ("Vindhyachal",25.1300m, 82.5800m),
                }
            },
            new {
                Name = "Moradabad", Lat = 28.8386m, Lng = 78.7733m,
                Cities = new[] {
                    ("Moradabad",   28.8386m, 78.7733m),
                    ("Sambhal",     28.5873m, 78.5686m),
                    ("Rampur",      28.8186m, 79.0259m),
                    ("Thakurdwara", 29.0423m, 78.6147m),
                    ("Bilari",      28.6244m, 78.8323m),
                    ("Kundarki",    28.7200m, 79.1000m),
                    ("Kanth",       28.7900m, 78.6300m),
                    ("Suar",        28.9000m, 79.0200m),
                    ("Amroha",      28.9045m, 78.4676m),
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
                    ("Jansath",      29.2900m, 77.7700m),
                    ("Charthawal",   29.5300m, 77.5600m),
                    ("Purqazi",      29.6200m, 77.8100m),
                    ("Bhopa",        29.3200m, 77.8100m),
                }
            },
            new {
                Name = "Pilibhit", Lat = 28.6311m, Lng = 79.8047m,
                Cities = new[] {
                    ("Pilibhit",  28.6311m, 79.8047m),
                    ("Puranpur",  28.5149m, 80.1467m),
                    ("Bisalpur",  28.2974m, 79.8013m),
                    ("Barkhera",  28.5693m, 79.8829m),
                    ("Kalinagar", 28.6500m, 79.6700m),
                    ("Marori",    28.7300m, 80.0100m),
                    ("Amariya",   28.4800m, 79.9400m),
                    ("Bilsanda",  28.3900m, 79.7500m),
                }
            },
            new {
                Name = "Pratapgarh", Lat = 25.8996m, Lng = 81.9845m,
                Cities = new[] {
                    ("Pratapgarh",   25.8996m, 81.9845m),
                    ("Kunda",        25.7191m, 81.5169m),
                    ("Rampur Kunda", 25.7438m, 81.8979m),
                    ("Lalganj",      25.9200m, 81.9100m),
                    ("Patti",        25.9200m, 82.2100m),
                    ("Raniganj",     25.6200m, 82.0300m),
                    ("Mandhata",     25.9700m, 82.0600m),
                    ("Sandwa",       25.8100m, 81.8600m),
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
                    ("Naini",     25.4200m, 81.8800m),
                    ("Kareli",    25.5200m, 81.8600m),
                    ("Soraon",    25.5900m, 81.9600m),
                    ("Shankargarh",25.1900m, 81.7400m),
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
                    ("Tiloi",     26.2800m, 81.5100m),
                    ("Mahrajganj",26.0700m, 81.3800m),
                    ("Khiron",    26.1600m, 81.1800m),
                    ("Harchandpur",26.3600m, 81.3400m),
                }
            },
            new {
                Name = "Rampur", Lat = 28.8186m, Lng = 79.0259m,
                Cities = new[] {
                    ("Rampur",   28.8186m, 79.0259m),
                    ("Milak",    28.6513m, 79.2367m),
                    ("Swar",     28.7593m, 79.1854m),
                    ("Bilaspur", 29.1900m, 79.0200m),
                    ("Tanda",    28.9700m, 78.9200m),
                    ("Shahabad", 28.7600m, 79.1200m),
                    ("Chamraua", 28.9400m, 79.0800m),
                    ("Paswara",  28.8700m, 78.8900m),
                }
            },
            new {
                Name = "Saharanpur", Lat = 29.9680m, Lng = 77.5510m,
                Cities = new[] {
                    ("Saharanpur",      29.9680m, 77.5510m),
                    ("Deoband",         29.6940m, 77.6790m),
                    ("Gangoh",          29.7819m, 77.2611m),
                    ("Rampur Maniharan",29.8214m, 77.4012m),
                    ("Behat",           30.3800m, 77.7600m),
                    ("Nakur",           29.9200m, 77.3500m),
                    ("Muzaffarabad",    30.0500m, 77.6700m),
                    ("Sarsawa",         29.8400m, 77.4100m),
                }
            },
            new {
                Name = "Sambhal", Lat = 28.5873m, Lng = 78.5686m,
                Cities = new[] {
                    ("Sambhal",  28.5873m, 78.5686m),
                    ("Chandausi",28.4535m, 78.7768m),
                    ("Gunnaur",  28.5149m, 78.4523m),
                    ("Rajpura",  28.4918m, 78.6134m),
                    ("Bahjoi",   28.4600m, 78.6300m),
                    ("Asmaoli",  28.5400m, 78.4700m),
                    ("Panwari",  28.6100m, 78.3900m),
                    ("Sular",    28.6700m, 78.5200m),
                }
            },
            new {
                Name = "Sant Kabir Nagar", Lat = 26.7741m, Lng = 83.0757m,
                Cities = new[] {
                    ("Khalilabad",   26.7741m, 83.0757m),
                    ("Mehdawal",     27.0015m, 83.1219m),
                    ("Baghauli",     26.9134m, 83.0048m),
                    ("Bakhira",      26.9400m, 83.1100m),
                    ("Hainsar Bazar",27.0500m, 83.2100m),
                    ("Sohagi",       27.0900m, 83.0700m),
                    ("Semriyawan",   26.8500m, 83.0200m),
                }
            },
            new {
                Name = "Shahjahanpur", Lat = 27.8840m, Lng = 79.9046m,
                Cities = new[] {
                    ("Shahjahanpur",27.8840m, 79.9046m),
                    ("Tilhar",      27.9648m, 79.7337m),
                    ("Powayan",     28.0836m, 79.9638m),
                    ("Jalalabad",   27.7284m, 79.6826m),
                    ("Katra",       27.9100m, 79.9900m),
                    ("Banda",       27.8200m, 80.3800m),
                    ("Nigohi",      27.9700m, 79.8200m),
                    ("Madnapur",    27.8600m, 80.2300m),
                }
            },
            new {
                Name = "Shamli", Lat = 29.4497m, Lng = 77.3128m,
                Cities = new[] {
                    ("Shamli",      29.4497m, 77.3128m),
                    ("Kairana",     29.3964m, 77.2020m),
                    ("Thana Bhawan",29.5864m, 77.4005m),
                    ("Budhana",     29.2882m, 77.4786m),
                    ("Kandhla",     29.3200m, 77.2600m),
                    ("Lisari Gate", 29.4300m, 77.2600m),
                    ("Un",          29.5400m, 77.3600m),
                    ("Jhinjhana",   29.5100m, 77.2200m),
                }
            },
            new {
                Name = "Shrawasti", Lat = 27.7023m, Lng = 81.9459m,
                Cities = new[] {
                    ("Bhinga",    27.7023m, 81.9459m),
                    ("Ikauna",    27.5879m, 82.1083m),
                    ("Huzoorpur", 27.8834m, 81.7869m),
                    ("Jamunaha",  27.9400m, 82.0800m),
                    ("Gilaula",   27.7700m, 82.0300m),
                    ("Sirsia",    27.6800m, 82.0500m),
                    ("Rahimpur",  27.6200m, 81.9800m),
                }
            },
            new {
                Name = "Siddharthnagar", Lat = 27.3097m, Lng = 82.7411m,
                Cities = new[] {
                    ("Naugarh",      27.3459m, 83.1148m),
                    ("Shohratgarh",  27.2481m, 83.1947m),
                    ("Domariyaganj", 27.3097m, 82.7411m),
                    ("Banhara Ghat", 27.2135m, 83.4529m),
                    ("Barhni",       27.4100m, 82.8200m),
                    ("Itwa",         27.2100m, 83.0600m),
                    ("Khunwa",       27.3400m, 83.3500m),
                    ("Bansi",        27.1800m, 83.0600m),
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
                    ("Maholi",     27.3900m, 80.5500m),
                    ("Reusa",      27.4500m, 80.8900m),
                    ("Khairabad",  27.5300m, 80.7300m),
                    ("Sidhauli",   27.4300m, 80.8300m),
                }
            },
            new {
                Name = "Sonbhadra", Lat = 24.6840m, Lng = 83.0683m,
                Cities = new[] {
                    ("Robertsganj", 24.6840m, 83.0683m),
                    ("Obra",        24.4535m, 82.9879m),
                    ("Renukoot",    24.2068m, 83.0423m),
                    ("Pipri",       24.5183m, 83.2347m),
                    ("Anpara",      24.1900m, 82.8200m),
                    ("Chopan",      24.4100m, 82.4900m),
                    ("Dudhi",       24.2000m, 83.2200m),
                    ("Babhani",     24.5700m, 83.4100m),
                }
            },
            new {
                Name = "Sultanpur", Lat = 26.2648m, Lng = 82.0727m,
                Cities = new[] {
                    ("Sultanpur", 26.2648m, 82.0727m),
                    ("Lambua",    26.1979m, 82.1843m),
                    ("Kadipur",   26.1346m, 82.3179m),
                    ("Amethi",    26.1486m, 81.7007m),
                    ("Dostpur",   26.3000m, 82.2700m),
                    ("Motipur",   26.2100m, 82.1500m),
                    ("Kurebhar",  26.2400m, 82.3400m),
                    ("Baldirai",  26.1000m, 82.0200m),
                }
            },
            new {
                Name = "Unnao", Lat = 26.5468m, Lng = 80.4883m,
                Cities = new[] {
                    ("Unnao",               26.5468m, 80.4883m),
                    ("Purwa",               26.4548m, 80.7826m),
                    ("Bangarmau",           26.7284m, 80.2103m),
                    ("Hasanganj",           26.7133m, 80.5248m),
                    ("Safipur",             26.7349m, 80.3445m),
                    ("Nawabganj",           26.5600m, 80.3500m),
                    ("Bighapur",            26.8200m, 80.3300m),
                    ("Fatehpur Chaurasi",   26.6300m, 80.6400m),
                    ("Auras",               26.6600m, 80.7900m),
                }
            },
            new {
                Name = "Varanasi", Lat = 25.3176m, Lng = 82.9739m,
                Cities = new[] {
                    ("Varanasi",    25.3176m, 82.9739m),
                    ("Mughal Sarai",25.2816m, 83.1163m),
                    ("Ramnagar",    25.2777m, 83.0199m),
                    ("Pindra",      25.3957m, 83.0779m),
                    ("Cholapur",    25.2551m, 83.1864m),
                    ("Sewapuri",    25.2200m, 83.0800m),
                    ("Shivpur",     25.3000m, 83.0300m),
                    ("Arajiline",   25.3500m, 82.9800m),
                    ("Harahua",     25.4000m, 82.9200m),
                }
            },

            // ── WEST BENGAL ──────────────────────────────────────────────────────────
            new {
                Name = "Alipurduar", Lat = 26.4954m, Lng = 89.5352m,
                Cities = new[] {
                    ("Alipurduar",     26.4954m, 89.5352m),
                    ("Madarihat",      26.3842m, 89.3211m),
                    ("Kumargram",      26.3200m, 89.5700m),
                    ("Falakata",       26.5670m, 88.9634m),
                    ("Hoollongapur",   26.2400m, 89.7200m),
                }
            },
            new {
                Name = "Bankura", Lat = 23.8590m, Lng = 87.0688m,
                Cities = new[] {
                    ("Bankura",        23.8590m, 87.0688m),
                    ("Bishnupur",      23.7522m, 87.3226m),
                    ("Khatra",         24.0100m, 86.8600m),
                    ("Saltora",        23.9500m, 87.1200m),
                    ("Raipur",         23.7900m, 87.5600m),
                    ("Taldangra",      24.1200m, 87.4500m),
                    ("Vishnupur",      23.7500m, 87.3200m),
                }
            },
            new {
                Name = "Birbhum", Lat = 24.0989m, Lng = 87.2685m,
                Cities = new[] {
                    ("Suri",           24.0989m, 87.2685m),
                    ("Bolpur",         23.9694m, 87.4737m),
                    ("Santiniketan",   23.9556m, 87.6755m),
                    ("Rampurhat",      24.3200m, 87.1500m),
                    ("Nanoor",         24.2200m, 87.4300m),
                    ("Khoyrasol",      24.1500m, 87.0800m),
                    ("Nalhati",        24.1800m, 87.5200m),
                }
            },
            new {
                Name = "Cooch Behar", Lat = 26.3300m, Lng = 89.2400m,
                Cities = new[] {
                    ("Cooch Behar",    26.3300m, 89.2400m),
                    ("Haldibari",      26.5263m, 88.9842m),
                    ("Mathabhanga",    26.4700m, 89.5100m),
                    ("Sitalkuchi",     26.1900m, 89.4500m),
                    ("Dinhata",        26.3900m, 89.1500m),
                    ("Tufanganj",      26.2000m, 88.9700m),
                    ("Mekhliganj",     26.3600m, 89.3200m),
                }
            },
            new {
                Name = "Darjeeling", Lat = 27.0360m, Lng = 88.2605m,
                Cities = new[] {
                    ("Darjeeling",     27.0360m, 88.2605m),
                    ("Kurseong",       26.8823m, 88.2704m),
                    ("Kalimpong",      27.0731m, 88.4673m),
                    ("Siliguri",       26.7271m, 88.3953m),
                    ("Bagdogra",       26.8833m, 88.3667m),
                    ("Mirik",          27.0668m, 88.3219m),
                    ("Rangli Rangliot", 27.1800m, 88.3200m),
                    ("Matigara",       26.8000m, 88.4300m),
                    ("Lebong",         27.0200m, 88.2800m),
                    ("Bijanbari",      27.0900m, 88.1800m),
                    ("Pankhabari",     26.9100m, 88.2700m),
                }
            },
            new {
                Name = "Dinajpur", Lat = 25.6271m, Lng = 88.6385m,
                Cities = new[] {
                    ("Dinajpur",       25.6271m, 88.6385m),
                    ("Baishnab Nagar", 25.6700m, 88.5800m),
                    ("Balurghat",      25.2305m, 88.7869m),
                    ("Thakurgaon",     26.1500m, 88.4700m),
                    ("Kalipur",        25.5200m, 88.7500m),
                    ("Gangarampur",    25.4800m, 88.9200m),
                    ("Birol",          25.6100m, 88.4300m),
                }
            },
            new {
                Name = "East Midnapore", Lat = 22.3985m, Lng = 88.1768m,
                Cities = new[] {
                    ("Medinipur",      22.3985m, 88.1768m),
                    ("Contai",         21.9404m, 88.0042m),
                    ("Egra",           22.4500m, 88.2400m),
                    ("Ramnagar",       22.5800m, 88.1700m),
                    ("Haldia",         22.1772m, 88.0556m),
                    ("Tamluk",         22.2897m, 87.7559m),
                    ("Nandigram",      22.1400m, 88.1600m),
                }
            },
            new {
                Name = "Howrah", Lat = 22.5958m, Lng = 88.2636m,
                Cities = new[] {
                    ("Howrah",         22.5958m, 88.2636m),
                    ("Shibpur",        22.5660m, 88.2470m),
                    ("Bally",          22.6156m, 88.3744m),
                    ("Shyamnagar",     22.6200m, 88.4300m),
                    ("Uttarpara Kotrung", 22.6544m, 88.3686m),
                    ("Lalbag",         22.6000m, 88.3100m),
                    ("Panchla",        22.6700m, 88.4400m),
                    ("Amta",           22.5100m, 88.2500m),
                }
            },
            new {
                Name = "Hooghly", Lat = 23.0291m, Lng = 88.3947m,
                Cities = new[] {
                    ("Hooghly",        23.0291m, 88.3947m),
                    ("Serampore",      22.7500m, 88.3700m),
                    ("Chinsurah",      22.7608m, 88.3722m),
                    ("Arambagh",       23.4000m, 88.3600m),
                    ("Rishra",         22.8000m, 88.3800m),
                    ("Tarakeswar",     23.3500m, 88.3200m),
                    ("Pandua",         23.3800m, 88.5600m),
                }
            },
            new {
                Name = "Jalpaiguri", Lat = 26.5203m, Lng = 88.7253m,
                Cities = new[] {
                    ("Jalpaiguri",     26.5203m, 88.7253m),
                    ("Rajganj",        26.5500m, 88.9600m),
                    ("Mainaguri",      26.4500m, 88.8300m),
                    ("Nagrakata",      26.6700m, 88.5600m),
                    ("Dhupguri",       26.6100m, 88.7100m),
                    ("Malbazar",       26.8700m, 88.8200m),
                    ("Maynaguri",      26.4400m, 88.8500m),
                }
            },
            new {
                Name = "Jhargram", Lat = 22.8436m, Lng = 87.0836m,
                Cities = new[] {
                    ("Jhargram",       22.8436m, 87.0836m),
                    ("Nayagram",       22.6700m, 87.1200m),
                    ("Salboni",        22.7500m, 87.2300m),
                    ("Raghunathpur",   22.9800m, 86.8700m),
                    ("Sandilpur",      22.8300m, 87.3200m),
                }
            },
            new {
                Name = "Kolkata", Lat = 22.5726m, Lng = 88.3639m,
                Cities = new[] {
                    ("Kolkata",        22.5726m, 88.3639m),
                    ("Alipore",        22.5339m, 88.3587m),
                    ("Ballygunge",     22.5275m, 88.3750m),
                    ("Behala",         22.4900m, 88.3500m),
                    ("Bhowanipore",    22.5200m, 88.3600m),
                    ("Chetla",         22.4800m, 88.3600m),
                    ("Kalighat",       22.5141m, 88.3522m),
                }
            },
            new {
                Name = "Malda", Lat = 25.9545m, Lng = 88.1392m,
                Cities = new[] {
                    ("Malda",          25.9545m, 88.1392m),
                    ("English Bazar",  25.3076m, 88.2745m),
                    ("Haluaghat",      25.8600m, 88.5400m),
                    ("Ratua",          25.8200m, 88.3300m),
                    ("Kaliachak",      25.7700m, 88.0500m),
                    ("Sahibganj",      24.6200m, 87.8200m),
                }
            },
            new {
                Name = "Murshidabad", Lat = 24.1733m, Lng = 88.2534m,
                Cities = new[] {
                    ("Murshidabad",    24.1733m, 88.2534m),
                    ("Berhampore",     24.1019m, 88.2435m),
                    ("Domkal",         24.4236m, 88.5909m),
                    ("Khagra",         24.4100m, 88.4300m),
                    ("Raghunathganj",  24.1600m, 88.4200m),
                    ("Sagarbari",      24.3800m, 88.1700m),
                    ("Jiaganj",        24.1800m, 88.3000m),
                }
            },
            new {
                Name = "Nadia", Lat = 23.6345m, Lng = 88.4329m,
                Cities = new[] {
                    ("Krishnanagar",   23.4016m, 88.4827m),
                    ("Santipur",       23.6345m, 88.4329m),
                    ("Ranaghat",       23.9433m, 88.6337m),
                    ("Kalyani",        22.9700m, 88.4300m),
                    ("Nabadwip",       23.5900m, 88.1900m),
                    ("Majdia",         23.4600m, 88.7500m),
                }
            },
            new {
                Name = "North 24 Parganas", Lat = 22.9456m, Lng = 88.4749m,
                Cities = new[] {
                    ("Barrackpur",     22.7653m, 88.3742m),
                    ("Barasat",        22.7192m, 88.4734m),
                    ("Asansol",        23.6832m, 86.9697m),
                    ("Bidhannagar",    22.5800m, 88.3800m),
                    ("Bongaon",        22.8700m, 88.8100m),
                    ("Garia",          22.5200m, 88.5100m),
                    ("Dum Dum",        22.6400m, 88.4400m),
                }
            },
            new {
                Name = "Paschim Medinipur", Lat = 22.7927m, Lng = 87.5302m,
                Cities = new[] {
                    ("Medinipur",      22.7927m, 87.5302m),
                    ("Ghatal",         23.2500m, 87.5600m),
                    ("Debra",          22.6000m, 87.2500m),
                    ("Kharagpur",      22.3268m, 87.3269m),
                    ("Sabang",         22.4600m, 87.1800m),
                    ("Rangpur",        22.8500m, 87.4200m),
                }
            },
            new {
                Name = "Purba Bardhaman", Lat = 23.8090m, Lng = 87.6585m,
                Cities = new[] {
                    ("Bardhaman",      23.8090m, 87.6585m),
                    ("Asansol",        23.6832m, 86.9697m),
                    ("Durgapur",       23.6214m, 87.3158m),
                    ("Katwa",          23.9700m, 87.8900m),
                    ("Kalna",          23.6300m, 88.1500m),
                    ("Bolpur",         23.9694m, 87.4737m),
                }
            },
            new {
                Name = "Purulia", Lat = 23.5250m, Lng = 86.2658m,
                Cities = new[] {
                    ("Purulia",        23.5250m, 86.2658m),
                    ("Manbazar",       23.4200m, 86.4600m),
                    ("Raghunathpur",   23.6600m, 86.3900m),
                    ("Jamshedpur",     22.8046m, 84.8304m),
                    ("Para",           23.9100m, 86.2200m),
                    ("Bagmundi",       23.7200m, 86.0700m),
                }
            },
            new {
                Name = "South 24 Parganas", Lat = 21.9850m, Lng = 88.4700m,
                Cities = new[] {
                    ("Alipur",         21.9850m, 88.4700m),
                    ("Canning",        22.0400m, 88.5600m),
                    ("Gosaba",         21.8300m, 88.7800m),
                    ("Diamond Harbour", 22.1920m, 88.2066m),
                    ("Jaynagar",       21.9600m, 88.3400m),
                    ("Kakdwip",        21.8600m, 88.1100m),
                    ("Falta",          22.1200m, 88.1600m),
                }
            },
            new {
                Name = "Uttar Dinajpur", Lat = 26.1500m, Lng = 88.4700m,
                Cities = new[] {
                    ("Thakurgaon",     26.1500m, 88.4700m),
                    ("Raiganj",        25.5900m, 88.1200m),
                    ("Itahar",         26.0800m, 88.5600m),
                    ("Hemtabad",       26.1200m, 88.2300m),
                    ("Kishangunj",     25.2800m, 87.9400m),
                }
            },
            new {
                Name = "Paschim Bardhaman", Lat = 23.2500m, Lng = 87.1200m,
                Cities = new[] {
                    ("Asansol",        23.6832m, 86.9697m),
                    ("Durgapur",       23.6214m, 87.3158m),
                    ("Kulti",          23.8500m, 86.9300m),
                    ("Jamuria",        23.7700m, 86.8200m),
                    ("Damodar",        23.6300m, 87.0400m),
                    ("Pandabeswar",    23.4500m, 86.9900m),
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

    private static async Task SeedPaymentFeatureAsync(ApplicationDbContext db)
    {
        if (await db.PaymentFeatures.AnyAsync()) return;

        var paymentFeature = new PaymentFeature
        {
            Id = Guid.NewGuid(),
            IsEnabled = true,
            FreePlanDays = 10,
            FreePlanRoomLimit = 1,
            PaidPlanPrice = 99,
            PaidPlanDays = 30,
            PaidPlanRoomLimit = 2,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.PaymentFeatures.Add(paymentFeature);
        await db.SaveChangesAsync();
    }
}
