using System.Text.Json.Serialization;

namespace RentNearBy.Core.DTOs.Owner;

// Contract 2.3 (P1..P6), 2.5 (T1..T5) and AU-14a. Owner-only shapes except PriceChipDto, which is the traveller chip "15% off".

public sealed record SpecialPriceItemDto(
    Guid Id, DateOnly DateFrom, DateOnly DateTo, int Nights, string Direction, int Percent, string? Label,
    bool IsPaused, bool PausedByPlan, bool Live, int Version);

public sealed record SpecialPriceRoomDto(Guid Id, string RoomName, int BasePrice);

// P1. StateName (FB-26): the stay's state, so the app offers that state's festivals as chips (null = national only).
public sealed record SpecialPriceListDto(
    IReadOnlyList<SpecialPriceItemDto> Items, IReadOnlyList<MeterDto> Meters, int SavesLeftToday,
    IReadOnlyList<SpecialPriceRoomDto> Rooms, bool PaymentEnabled, string? StateName = null);

// P2..P6 answer.
public sealed record SpecialPriceWriteDto(SpecialPriceItemDto Item, IReadOnlyList<MeterDto> Meters, int SavesLeftToday);

// error.data of SpecialPriceOverlap: the rule in the way (the "Edit Diwali week" button).
public sealed record SpecialPriceOverlapDto(Guid Id, string? Label, DateOnly DateFrom, DateOnly DateTo);

public sealed class CreateSpecialPriceRequest
{
    public Guid Id { get; set; }
    public DateOnly DateFrom { get; set; }
    public DateOnly DateTo { get; set; }
    public string Direction { get; set; } = string.Empty;
    public int Percent { get; set; }
    public Guid? FestivalDateId { get; set; }
}

public sealed class UpdateSpecialPriceRequest
{
    public DateOnly DateFrom { get; set; }
    public DateOnly DateTo { get; set; }
    public string Direction { get; set; } = string.Empty;
    public int Percent { get; set; }
    public Guid? FestivalDateId { get; set; }
    public int Version { get; set; }
}

public sealed class PauseSpecialPriceRequest
{
    public bool Paused { get; set; }
    public int Version { get; set; }
}

public sealed class LiveSpecialPriceRequest
{
    public int Version { get; set; }
}

// Traveller chip: only kind "Off" exists ("N% off" for a Lower rule); never a festival name, never on a Higher rule.
public sealed record PriceChipDto(string Kind, string Text)
{
    public const string Off = "Off";

    public static PriceChipDto? Of(int? lowerPercent) => lowerPercent is { } p and > 0 ? new PriceChipDto(Off, $"{p}% off") : null;
}

// Cache-only rows riding on the stay list candidates (contract 8.3): active rules (not paused, not plan-paused) and blocks.
public sealed class StayRuleRow
{
    [JsonPropertyName("from")] public DateOnly From { get; set; }
    [JsonPropertyName("to")] public DateOnly To { get; set; }
    [JsonPropertyName("lower")] public bool Lower { get; set; }
    [JsonPropertyName("percent")] public int Percent { get; set; }
}

public sealed class StayBlockRow
{
    [JsonPropertyName("from")] public DateOnly From { get; set; }
    [JsonPropertyName("to")] public DateOnly To { get; set; }
}

// AU-14a: one segment per run of nights with the same per-night price per room line; owner DTO only.
public sealed record ListedBreakdownLineDto(string RoomName, int Units, int Nights, int PerNight, string? SpecialLabel);
