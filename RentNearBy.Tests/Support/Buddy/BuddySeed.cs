using RentNearBy.Core.Entities.Buddy;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Tests.Support.Buddy;

// Seed builders for query-count and integration tests. Use a plain context (never the counting one). Fresh ids every call.
public static class BuddySeed
{
    private static int _phone = Random.Shared.Next(10_000_000, 80_000_000);

    public static async Task<List<Guid>> UsersAsync(ApplicationDbContext db, int n, string namePrefix = "Person")
    {
        var now = DateTime.UtcNow;
        var ids = new List<Guid>();
        for (var i = 0; i < n; i++)
        {
            var id = Guid.NewGuid();
            var number = Interlocked.Increment(ref _phone);
            db.Users.Add(new User
            {
                Id = id, PhoneNumber = "9" + number.ToString("D9"), Name = $"{namePrefix} {i + 1}", IsActive = true, IsPhoneVerified = true,
                Username = Usernames.Format(10_000_000 + number % 89_999_999),
                RoleId = UserRoles.SeekerId, RoleConfirmedAt = now, CreatedAt = now, UpdatedAt = now,
            });
            ids.Add(id);
        }
        await db.SaveChangesAsync();
        return ids;
    }

    public static async Task BuddiesAsync(ApplicationDbContext db, Guid a, IEnumerable<Guid> others, bool seen = true)
    {
        var now = DateTime.UtcNow;
        foreach (var b in others)
        {
            db.Set<BuddyLink>().Add(new BuddyLink { UserId = a, BuddyUserId = b, CreatedAt = now, SeenAt = seen ? now : null });
            db.Set<BuddyLink>().Add(new BuddyLink { UserId = b, BuddyUserId = a, CreatedAt = now, SeenAt = seen ? now : null });
        }
        await db.SaveChangesAsync();
    }

    public static async Task<List<Guid>> RequestsAsync(
        ApplicationDbContext db, Guid from, IEnumerable<Guid> to, string status = BuddyRequestStatuses.Pending, TimeSpan? age = null)
    {
        var created = DateTime.UtcNow - (age ?? TimeSpan.Zero);
        var ids = new List<Guid>();
        foreach (var t in to)
        {
            var (low, high) = BuddyPairs.Order(from, t);
            var id = Guid.NewGuid();
            db.Set<BuddyRequest>().Add(new BuddyRequest
            {
                Id = id, FromUserId = from, ToUserId = t, PairLowId = low, PairHighId = high, Status = status,
                Source = BuddyRequestSources.Search, ClientRequestId = Guid.NewGuid(), CreatedAt = created,
                ExpiresAt = created + BuddyLimits.RequestExpiry,
                EndedAt = status == BuddyRequestStatuses.Pending || status == BuddyRequestStatuses.Accepted ? null : created,
            });
            ids.Add(id);
        }
        await db.SaveChangesAsync();
        return ids;
    }

    public static async Task<Guid> TripAsync(ApplicationDbContext db, Guid owner, int days = 3, DateOnly? start = null, string title = "Uttarakhand Hills Trip")
    {
        var id = Guid.NewGuid();
        var first = start ?? IstTime.TodayIst(DateTime.UtcNow).AddDays(10);
        db.Trips.Add(new Trip
        {
            Id = id, OwnerUserId = owner, CityKey = 210000001, CityName = "Nainital", Title = title, Status = TripStatuses.Planned,
            PlanJson = "{}", DayCount = days, SpotCount = days, StartDate = first, EndDate = first.AddDays(days - 1),
            InviteCode = Guid.NewGuid(), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return id;
    }

    public static async Task<List<Guid>> MembersAsync(ApplicationDbContext db, Guid trip, Guid owner, IEnumerable<Guid> viewers, string status = TripMemberStatuses.Joined)
    {
        var now = DateTime.UtcNow;
        var ids = new List<Guid>();
        var i = 0;
        foreach (var v in viewers)
        {
            var id = Guid.NewGuid();
            var joined = status is TripMemberStatuses.Joined or TripMemberStatuses.Left or TripMemberStatuses.Removed;
            db.Set<TripMember>().Add(new TripMember
            {
                Id = id, TripId = trip, UserId = v, Status = status, InvitedByUserId = owner,
                InvitedAt = now.AddMinutes(-60 + i), InviteExpiresAt = now + BuddyLimits.InviteExpiry,
                JoinedAt = joined ? now.AddMinutes(-30 + i) : null,
                EndedAt = status is TripMemberStatuses.Joined or TripMemberStatuses.Invited ? null : now,
                CreatedAt = now, UpdatedAt = now,
            });
            ids.Add(id);
            i++;
        }
        await db.SaveChangesAsync();
        return ids;
    }

    // n live expenses, equal split over sharers (payer = first sharer), day cycling 1..days.
    public static async Task<List<Guid>> ExpensesAsync(ApplicationDbContext db, Guid trip, Guid owner, int n, IReadOnlyList<Guid> sharers, int days = 3, int amount = 1000)
    {
        var now = DateTime.UtcNow;
        var ids = new List<Guid>();
        for (var i = 0; i < n; i++)
        {
            var id = Guid.NewGuid();
            var split = TripExpenseEngine.EqualSplit(amount, sharers, sharers, sharers[0]);
            db.Set<TripExpense>().Add(new TripExpense
            {
                Id = id, TripId = trip, Amount = amount, Title = $"Expense {i + 1}", Category = ExpenseCategories.Food,
                DayNumber = (short)(i % days + 1), PaidByUserId = sharers[0], SplitMode = SplitModes.AllTravellers,
                LeftoverUserId = split.LeftoverUserId, Version = 1, CreatedByUserId = owner, UpdatedByUserId = owner,
                CreatedOpId = Guid.NewGuid(), LastOpId = Guid.NewGuid(), CreatedAt = now.AddSeconds(i), UpdatedAt = now.AddSeconds(i),
            });
            foreach (var s in split.Shares)
                db.Set<TripExpenseShare>().Add(new TripExpenseShare { ExpenseId = id, UserId = s.UserId, TripId = trip, Share = s.Share });
            ids.Add(id);
        }
        await db.SaveChangesAsync();
        return ids;
    }

    public static async Task<Guid> PaymentAsync(ApplicationDbContext db, Guid trip, Guid from, Guid to, int amount, string state = PaymentStates.SaidPaid)
    {
        var now = DateTime.UtcNow;
        var id = Guid.NewGuid();
        var ended = state is PaymentStates.Withdrawn or PaymentStates.NotReceived;
        db.Set<TripPayment>().Add(new TripPayment
        {
            Id = id, TripId = trip, FromUserId = from, ToUserId = to, Amount = amount, State = state,
            SaidPaidAt = now, SaidPaidByUserId = from,
            ReceivedAt = state == PaymentStates.Received ? now : null, ReceivedByUserId = state == PaymentStates.Received ? to : null,
            EndedAt = ended ? now : null, EndedByUserId = ended ? from : null, CreatedAt = now,
        });
        await db.SaveChangesAsync();
        return id;
    }

    public static async Task NoticesAsync(ApplicationDbContext db, Guid user, IEnumerable<(string Kind, Guid TripId)> notices)
    {
        var now = DateTime.UtcNow;
        foreach (var (kind, tripId) in notices)
            db.Set<UserNotice>().Add(new UserNotice { Id = Guid.NewGuid(), UserId = user, Kind = kind, TripId = tripId, CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
    }
}
