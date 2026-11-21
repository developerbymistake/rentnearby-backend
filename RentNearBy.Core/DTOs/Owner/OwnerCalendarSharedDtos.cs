namespace RentNearBy.Core.DTOs.Owner;

// Kind-agnostic parts of the two owner calendars (contract 2.1). No money note member lives here (MoneyNotePrivacyGuardTests).
public sealed record CalendarFestivalDto(Guid Id, string Name, DateOnly DateFrom, DateOnly DateTo);

public sealed record CalendarReminderDto(bool Allowed, bool On);

// Server truth after a manual save (AU-10): bookings = other entries touching the nights, blocked = blocks touching them.
public sealed record CalendarOverlapDto(int Bookings, int Blocked);
