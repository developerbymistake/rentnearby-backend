namespace RentNearBy.Core.DTOs.Buddy;

public class PersonDto
{
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Username { get; set; }
    public string Initial { get; set; } = string.Empty;
}

// Status: Owner | Joined | Left | Removed | Deleted. A deleted account reads "Deleted user", last4 "", initial "?".
public class MoneyPersonDto
{
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Last4 { get; set; } = string.Empty;
    public string Initial { get; set; } = string.Empty;
    public bool IsMe { get; set; }
    public string Status { get; set; } = string.Empty;
}

public class PageDto<T>
{
    public IReadOnlyList<T> Items { get; set; } = [];
    public string? NextCursor { get; set; }
}

public class TravellerChipDto
{
    public Guid UserId { get; set; }
    public string Initial { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

public class TripExpenseCardDto
{
    public bool HasExpenses { get; set; }
    public long Spent { get; set; }
    public int MyShare { get; set; }
    public int? DayToday { get; set; }
    public int OpenPayments { get; set; }
    public bool Settled { get; set; }
}

// The sharing block of GET /trips/{id} (owner), contract 2.2 "Changed existing routes".
public class TripSharingBlockDto
{
    public int TravellerCount { get; set; }
    public int ViewerCount { get; set; }
    public int PendingInvites { get; set; }
    public List<TravellerChipDto> FirstTravellers { get; set; } = [];
    public TripExpenseCardDto Expenses { get; set; } = new();
}
