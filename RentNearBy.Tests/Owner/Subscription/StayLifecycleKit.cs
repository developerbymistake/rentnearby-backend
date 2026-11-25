using RentNearBy.Core.Models.Owner;

namespace RentNearBy.Tests.Owner.Subscription;

// Pure helpers of the Stay lifecycle tests: rows as the owner added them, and the "write back" of a plan choice, so one test can
// chain expire, re-subscribe, expire again exactly like the daily pass and the grant / expiry effects do on the database.
internal static class StayLifecycleKit
{
    public static readonly DateTime Base = new(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc);

    // Rooms: RoomIndex 1..n in the order they were added.
    public static List<PlanRow> Rooms(int n) => Enumerable.Range(1, n).Select(i => new PlanRow(Guid.NewGuid(), false, i)).ToList();

    // Photos: PhotoOrder 0..n-1, the first is the cover.
    public static List<PlanRow> Photos(int n) => Enumerable.Range(0, n).Select(i => new PlanRow(Guid.NewGuid(), false, i)).ToList();

    public static List<PlanJobRow> Jobs(int n) => Enumerable.Range(0, n).Select(i => new PlanJobRow(Guid.NewGuid(), false, Base.AddMinutes(i))).ToList();

    public static List<PlanRuleRow> Rules(int n) => Enumerable.Range(0, n).Select(i => new PlanRuleRow(Guid.NewGuid(), false, Base.AddMinutes(i))).ToList();

    public static List<PlanRow> Apply(IReadOnlyList<PlanRow> rows, IReadOnlyList<PlanRowTarget> targets)
        => rows.Select(r =>
        {
            var t = targets.Single(x => x.Id == r.Id);
            return new PlanRow(r.Id, t.Hidden, t.Index);
        }).ToList();

    public static List<PlanRow> SettleIndexed(IReadOnlyList<PlanRow> rows, int limit, bool hide)
        => Apply(rows, PlanRowChoice.ChooseIndexed(rows, limit, hide));

    public static List<PlanRow> SettlePhotos(IReadOnlyList<PlanRow> rows, int limit, bool hide)
        => Apply(rows, PlanRowChoice.ChoosePhotos(rows, limit, hide));

    public static List<PlanJobRow> SettleJobs(IReadOnlyList<PlanJobRow> rows, int limit, bool hide)
    {
        var map = PlanJobChoice.Inactive(rows, limit, hide);
        return rows.Select(j => j with { Inactive = map[j.Id] }).ToList();
    }

    public static List<PlanRuleRow> SettleRules(IReadOnlyList<PlanRuleRow> rows, int limit, bool hide)
    {
        var map = PlanRuleChoice.PausedByPlan(rows, limit, hide);
        return rows.Select(r => r with { PausedByPlan = map[r.Id] }).ToList();
    }

    public static List<Guid> VisibleIds(IEnumerable<PlanRow> rows) => rows.Where(r => !r.Hidden).OrderBy(r => r.Index).Select(r => r.Id).ToList();

    public static List<Guid> InOrder(IEnumerable<PlanRow> rows) => rows.OrderBy(r => r.Index).Select(r => r.Id).ToList();

    public static List<PlanRow> ById(IEnumerable<PlanRow> rows) => rows.OrderBy(r => r.Id).ToList();
}
