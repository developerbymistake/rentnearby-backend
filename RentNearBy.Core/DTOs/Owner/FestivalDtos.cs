namespace RentNearBy.Core.DTOs.Owner;

// F1: festival rows for owner chips, names and reminders. Never on a traveller screen.
public sealed record FestivalDto(Guid Id, string Name, DateOnly DateFrom, DateOnly DateTo);

public sealed record FestivalListDto(IReadOnlyList<FestivalDto> Items);

// A2 admin list row.
public sealed record AdminFestivalDto(
    Guid Id, string Name, DateOnly DateFrom, DateOnly DateTo, string? StateName, bool IsActive, DateTime CreatedAt, DateTime UpdatedAt);

public sealed record AdminFestivalStatesDto(IReadOnlyList<string> Items);

// POST (client id, idempotent) and PUT. StateName null = all India.
public sealed class AdminFestivalRequest
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateOnly DateFrom { get; set; }
    public DateOnly DateTo { get; set; }
    public string? StateName { get; set; }
    public bool IsActive { get; set; } = true;
}
