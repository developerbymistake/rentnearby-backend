using RentNearBy.Core.Models.Owner;

namespace RentNearBy.Core.DTOs.Owner;

// Contract 2.0 shared shapes (camelCase on the wire). With payment OFF the app prints no limit text: paymentEnabled rides on every owner payload.
public sealed record MeterDto(string Key, int Used, int Limit);

public sealed record WindowDto(DateOnly UpcomingTo, DateOnly MonthFrom, DateOnly MonthTo, DateOnly ManualFrom, DateOnly ManualTo, bool Locked)
{
    public static WindowDto From(OwnerCalendarWindowValue w) => new(w.UpcomingTo, w.MonthFrom, w.MonthTo, w.ManualFrom, w.ManualTo, w.Locked);
}

// error.data of every limit refusal (contract 3.2, appendix A).
public sealed record PlanLimitRefusalDto(string Key, int Limit, int Used, bool CanUpgrade);

// error.data of the two daily counters (SpecialPriceSavesLimit, PlanVisibleLimit).
public sealed record DailyLimitRefusalDto(int Limit, int Used);

// error.data of StaleEdit: the stored row.
public sealed record StaleEditDto<T>(T Current);

public sealed record OwnerPageDto<T>(IReadOnlyList<T> Items, string? NextCursor);
