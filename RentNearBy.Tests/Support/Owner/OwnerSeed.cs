using RentNearBy.Core.Entities.Owner;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Tests.Support.Owner;

// Seed builders of the owner roles for integration, race and query-count tests (contract 12.1). Use a plain context, never the
// counting one. Fresh ids every call; lookups (stay type, registration type) are created once per call and returned.
public static class OwnerSeed
{
    private static int _phone = Random.Shared.Next(10_000_000, 80_000_000);

    public sealed record OwnerRow(Guid UserId, Guid ListingId, string Kind);

    public static async Task<Guid> UserAsync(ApplicationDbContext db, Guid roleId, string name = "Owner")
    {
        var now = DateTime.UtcNow;
        var id = Guid.NewGuid();
        var number = Interlocked.Increment(ref _phone);
        db.Users.Add(new User
        {
            Id = id, PhoneNumber = "9" + number.ToString("D9"), Name = name, IsActive = true, IsPhoneVerified = true,
            Username = Usernames.Format(10_000_000 + number % 89_999_999),
            RoleId = roleId, RoleConfirmedAt = now, CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return id;
    }

    public static async Task<OwnerRow> StayOwnerAsync(ApplicationDbContext db, string status = StayStatuses.Approved, string name = "Lake View Stay")
    {
        var userId = await UserAsync(db, UserRoles.StayOwnerId, "Host");
        var tag = Guid.NewGuid().ToString("N")[..8];
        var now = DateTime.UtcNow;
        var registration = new StayRegistrationType
        {
            Id = Guid.NewGuid(), Code = $"ow{tag}", Name = $"Owner registration {tag}", SortOrder = 999, IsActive = true, CreatedAt = now,
        };
        db.StayRegistrationTypes.Add(registration);
        var stayId = Guid.NewGuid();
        db.StayListings.Add(new StayListing
        {
            Id = stayId, UserId = userId, Name = name,
            RegistrationTypeId = registration.Id, RegistrationTypeName = registration.Name, RegistrationNumber = "OW-1",
            Latitude = 29.3919m, Longitude = 79.4542m, Status = status, EverApproved = status == StayStatuses.Approved,
            AcceptingGuests = true, MinPricePerNight = 3000, CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return new OwnerRow(userId, stayId, ListingKinds.Stay);
    }

    public static async Task<OwnerRow> TaxiDriverAsync(ApplicationDbContext db, string status = TaxiStatuses.Approved)
    {
        var userId = await UserAsync(db, UserRoles.TaxiDriverId, "Driver");
        var now = DateTime.UtcNow;
        var taxiId = Guid.NewGuid();
        db.TaxiListings.Add(new TaxiListing
        {
            Id = taxiId, UserId = userId, DrivingLicenceNumberEnc = "owner-test", DateOfBirth = new DateOnly(1985, 1, 1),
            Latitude = 29.3919m, Longitude = 79.4542m, ContactPhone = "9000000001", Seats = 6,
            Status = status, EverApproved = status == TaxiStatuses.Approved, IsAvailable = true, CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return new OwnerRow(userId, taxiId, ListingKinds.Taxi);
    }

    public static async Task<List<StayRoom>> RoomsAsync(ApplicationDbContext db, Guid stayId, int visible, int hidden = 0, int price = 3000)
    {
        var now = DateTime.UtcNow;
        string[] names = ["Standard", "Deluxe", "Premium", "Suite", "Family Room", "Executive", "Dormitory"];
        var rows = new List<StayRoom>();
        for (var i = 0; i < visible + hidden; i++)
        {
            var isHidden = i >= visible;
            rows.Add(new StayRoom
            {
                Id = Guid.NewGuid(), StayListingId = stayId,
                RoomIndex = (short)(isHidden ? PlanRowBands.HiddenBase + 1 + (i - visible) : i + 1),
                PricePerNight = price, MaxGuests = 2, RoomName = names[i % names.Length], Meals = "None",
                HiddenByPlan = isHidden, CreatedAt = now, UpdatedAt = now,
            });
        }
        db.StayRooms.AddRange(rows);
        await db.SaveChangesAsync();
        return rows;
    }

    public static async Task<List<StayPhoto>> PhotosAsync(ApplicationDbContext db, Guid stayId, int visible, int hidden = 0)
    {
        var rows = Enumerable.Range(0, visible + hidden).Select(i => new StayPhoto
        {
            Id = Guid.NewGuid(), StayListingId = stayId, PhotoUrl = $"https://example.test/p{i}.jpg", FilePath = $"p{i}",
            PhotoOrder = i, HiddenByPlan = i >= visible, UploadedAt = DateTime.UtcNow,
        }).ToList();
        db.StayPhotos.AddRange(rows);
        await db.SaveChangesAsync();
        return rows;
    }

    public static async Task<List<CalendarEntry>> ManualEntriesAsync(
        ApplicationDbContext db, OwnerRow owner, int n, DateOnly firstDay, int stepDays = 1, int? finalAmount = null, int? advance = null,
        bool keepVisible = false)
    {
        var now = DateTime.UtcNow;
        var rows = Enumerable.Range(0, n).Select(i =>
        {
            var from = firstDay.AddDays(i * stepDays);
            return new CalendarEntry
            {
                Id = Guid.NewGuid(), OwnerUserId = owner.UserId, ListingKind = owner.Kind, ListingId = owner.ListingId,
                Kind = CalendarEntryKinds.Manual, DateFrom = from, DateTo = owner.Kind == ListingKinds.Stay ? from.AddDays(1) : from,
                Label = $"Phone booking {i + 1}", FinalAmount = finalAmount, AdvanceReceived = advance, CreatedAt = now.AddSeconds(i), UpdatedAt = now,
                KeepVisible = keepVisible,
            };
        }).ToList();
        db.Set<CalendarEntry>().AddRange(rows);
        await db.SaveChangesAsync();
        return rows;
    }

    public static async Task<CalendarEntry> AutoEntryAsync(
        ApplicationDbContext db, OwnerRow owner, string sourceType, Guid sourceId, DateOnly from, DateOnly to, Guid? travellerUserId = null,
        bool cancelled = false)
    {
        var now = DateTime.UtcNow;
        var row = new CalendarEntry
        {
            Id = Guid.NewGuid(), OwnerUserId = owner.UserId, ListingKind = owner.Kind, ListingId = owner.ListingId,
            Kind = CalendarEntryKinds.Auto, SourceType = sourceType, SourceId = sourceId, TravellerUserId = travellerUserId,
            DateFrom = from, DateTo = to, Label = "Neha Joshi", Cancelled = cancelled, CreatedAt = now, UpdatedAt = now,
        };
        db.Set<CalendarEntry>().Add(row);
        await db.SaveChangesAsync();
        return row;
    }

    public static async Task<List<StayBlockedRange>> BlocksAsync(ApplicationDbContext db, Guid stayId, int n, DateOnly firstNight, int nights = 2)
    {
        var now = DateTime.UtcNow;
        var rows = Enumerable.Range(0, n).Select(i => new StayBlockedRange
        {
            Id = Guid.NewGuid(), StayListingId = stayId, DateFrom = firstNight.AddDays(i * (nights + 1)),
            DateTo = firstNight.AddDays(i * (nights + 1) + nights - 1), Reason = "Family visit", CreatedAt = now, UpdatedAt = now,
        }).ToList();
        db.Set<StayBlockedRange>().AddRange(rows);
        await db.SaveChangesAsync();
        return rows;
    }

    public static async Task<List<StaySpecialPrice>> SpecialPricesAsync(
        ApplicationDbContext db, Guid stayId, int n, DateOnly firstNight, int nights = 3, string direction = SpecialPriceDirections.Lower,
        short percent = 15, bool pausedByPlan = false)
    {
        var now = DateTime.UtcNow;
        var rows = Enumerable.Range(0, n).Select(i => new StaySpecialPrice
        {
            Id = Guid.NewGuid(), StayListingId = stayId, DateFrom = firstNight.AddDays(i * (nights + 1)),
            DateTo = firstNight.AddDays(i * (nights + 1) + nights - 1), Direction = direction, Percent = percent,
            PausedByPlan = pausedByPlan, CreatedAt = now, UpdatedAt = now,
        }).ToList();
        db.Set<StaySpecialPrice>().AddRange(rows);
        await db.SaveChangesAsync();
        return rows;
    }

    public static async Task<FestivalDate> FestivalAsync(ApplicationDbContext db, string name, DateOnly from, DateOnly to, string? stateName = null)
    {
        var row = new FestivalDate
        {
            Id = Guid.NewGuid(), Name = name, DateFrom = from, DateTo = to, StateName = stateName, IsActive = true,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.Set<FestivalDate>().Add(row);
        await db.SaveChangesAsync();
        return row;
    }

    // An active plan row (Plus, trial or admin grant) for the owner, ending in 30 days.
    public static async Task<Guid> ActivePlanAsync(ApplicationDbContext db, Guid userId, string kind, Guid planId, string source = UserPlanSources.Prepaid)
    {
        var now = DateTime.UtcNow;
        var id = Guid.NewGuid();
        db.UserPlans.Add(new UserPlan
        {
            Id = id, UserId = userId, FeatureKey = kind, PlanId = planId, Source = source, Status = UserPlanStatuses.Active,
            StartsAt = now.AddDays(-1), EndsAt = now.AddDays(30), CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return id;
    }
}
