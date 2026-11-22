namespace RentNearBy.Core.Models.Owner;

// Contract 1.5. Plan numbers are not here: they come from PlanLimits.
public static class OwnerLimits
{
    public const int LabelMax = 60;
    public const int NoteMax = 200;
    public const int PlaceMax = 80;
    public const int ReasonMax = 60;
    public const int DetailMax = 120;
    public const int RoomNamesMax = 80;
    public const int FestivalNameMax = 40;
    public const int FestivalLabelMax = 40;
    public const int StateNameMax = 100;
    public const int PendingEditReasonMax = 300;
    public const int PlacesMaxCount = 20;
    public const int MoneyMin = 1;
    public const int MoneyMax = 9_900_000;
    public const int ManualBackDays = 7;
    // D-07: hard ceiling of manual rows per listing in the kept 90 days (past rows included), whatever the plan.
    public const int ManualRowsCeiling = 500;
    public const int PlusWindowDays = 183;
    public const int StayEntryMaxNights = 60;
    public const int BlockMaxNights = 90;
    public const int BlockStartWithinDays = 180;
    public const int SpecialMaxNights = 60;
    public const int SpecialStartWithinDays = 180;
    public const int SpecialSavesPerDay = 3;
    public const int PlanVisibleSavesPerDay = 3;
    public const int PastDays = 90;
    public const int PageSize = 20;
    public const int StaysChipWithinDays = 30;
    public const int FestivalStayLeadDays = 21;
    public const int FestivalTaxiLeadDays = 7;
    public const int FestivalWindowDays = 3;
    public const int FestivalMaxDays = 30;
    public const int CalendarRangeMaxDays = 45;
    public const int WeeklyMinShown = 10;
    public const int StatsKeepDays = 56;
    public const int RankBandKm = 5;
    public const int AnnounceLeadDays = 7;
    public const int EditResultStripDays = 7;
    public const int ListedStayTotalMin = 100;
    public const int SpecialBigOffPercent = 40;

    // Retention (days after the end date), contract 5.1.
    public const int CalendarDays = 90;
    public const int BlockedDays = 90;
    public const int SpecialDays = 30;
    public const int FestivalMarkDays = 30;
    public const int ReminderRunDays = 30;

    // Hard ceilings above every plan value (PlanKeyCatalog Max). StayLimits / TaxiLimits move to these with the plan check (lane BE3).
    public const int RoomsCeiling = 8;
    public const int PhotosCeiling = 12;
    public const int RoutesCeiling = 10;
}

// Hidden rows live in the index band above HiddenBase so a full replace of visible rows never collides with them (contract 3.3).
// Named PlanRowBands (not PlanRows) so lane BE3's Infrastructure PlanRows service never clashes with it.
public static class PlanRowBands
{
    public const int HiddenBase = 100;
    public const int RenumberShift = 1000;
}
