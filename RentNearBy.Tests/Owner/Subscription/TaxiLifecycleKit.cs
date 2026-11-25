using RentNearBy.Core.Models.Owner;
using RentNearBy.Tests.Owner.Be3;

namespace RentNearBy.Tests.Owner.Subscription;

internal static class TaxiLifecycleKit
{
    public const string Taxi = ListingKinds.Taxi;

    public static PlanLimits Free => Be3Kit.Free(Taxi);
    public static PlanLimits Plus => Be3Kit.Plus(Taxi);
    public static PlanLimits Off => Be3Kit.Off(Taxi);

    public static List<PlanRow> Routes(int count) => Enumerable.Range(1, count).Select(i => new PlanRow(Guid.NewGuid(), false, i)).ToList();

    public static List<PlanRow> Apply(IReadOnlyList<PlanRow> rows, PlanLimits limits)
        => Next(rows, PlanRowChoice.ChooseIndexed(rows, limits.Routes, limits.PaymentEnabled));

    public static List<PlanRow> Next(IReadOnlyList<PlanRow> rows, IEnumerable<PlanRowTarget> targets)
    {
        var map = targets.ToDictionary(t => t.Id);
        return rows.Select(r => new PlanRow(r.Id, map[r.Id].Hidden, map[r.Id].Index)).ToList();
    }

    public static List<Guid> VisibleInOrder(IEnumerable<PlanRow> rows) => rows.Where(r => !r.Hidden).OrderBy(r => r.Index).Select(r => r.Id).ToList();

    public static List<Guid> HiddenInOrder(IEnumerable<PlanRow> rows) => rows.Where(r => r.Hidden).OrderBy(r => r.Index).Select(r => r.Id).ToList();

    public static CalendarEntryRow Entry(DateOnly day, DateTime createdAt) => new()
    {
        Id = Guid.NewGuid(), Kind = CalendarEntryKinds.Manual, DateFrom = day, DateTo = day, Label = "Airport drop", CreatedAt = createdAt,
    };

    public static string Src(params string[] parts) => Be3Kit.Source(parts);
}
