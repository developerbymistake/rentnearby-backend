namespace RentNearBy.Core.Models.Owner;

// One plan-limited row of a listing (room, route or photo) as the choice sees it. Index = RoomIndex / RouteIndex, or PhotoOrder.
public readonly record struct PlanRow(Guid Id, bool Hidden, int Index);

// Where a row goes: visible rows hold 1..V (photos keep their order value), hidden rows the band above PlanRowBands.HiddenBase.
public readonly record struct PlanRowTarget(Guid Id, bool Hidden, int Index);

// Contract 3.2 / 3.3: the full-replace limit rule and which rows the plan keeps visible. Pure, no IO.
public static class PlanRowChoice
{
    // A save that adds above the limit is refused; one that keeps or shrinks an over-limit set passes (an edit never hits it).
    public static bool SaveAllowed(int requestCount, int limit, int visibleNow) => requestCount <= limit || requestCount <= visibleNow;

    // V1 / V2 are open while a row is hidden or would be hidden (more rows than the plan value).
    public static bool ChoiceNeeded(IReadOnlyCollection<PlanRow> rows, int limit) => rows.Count > Math.Max(0, limit) || rows.Any(r => r.Hidden);

    // Photo upload: refused when visible >= the plan value or all rows (hidden too) >= the hard ceiling.
    public static bool PhotoUploadAllowed(int visible, int all, int limit) => visible < limit && all < OwnerLimits.PhotosCeiling;

    // How many rows stay visible: everything while nothing may be hidden (payment OFF), else min(limit, rows).
    public static int VisibleCount(int rows, int limit, bool hide) => hide ? Math.Min(Math.Max(0, limit), rows) : rows;

    // Server choice (plan effects, reconcile) for rooms and routes: the last added stay. Rank by (Hidden, Index DESC, Id); rank <=
    // limit is kept, the rest hidden (a new row always takes the highest visible index, so highest index = last added). The
    // owner's own choice is never reshuffled (visible rows rank first). Both groups keep the order they were added in: rows from
    // the hidden band first (they are the older ones), then the visible ones. Kept rows take 1..V, hidden rows HiddenBase+1..,
    // so a new Plus restores the original order and the newest hidden row comes back first.
    public static IReadOnlyList<PlanRowTarget> ChooseIndexed(IEnumerable<PlanRow> rows, int limit, bool hide)
    {
        var ranked = rows.OrderBy(r => r.Hidden).ThenByDescending(r => r.Index).ThenBy(r => r.Id).ToList();
        var visible = VisibleCount(ranked.Count, limit, hide);
        return Number(OldOrder(ranked.Take(visible)), OldOrder(ranked.Skip(visible)));
    }

    private static IEnumerable<PlanRow> OldOrder(IEnumerable<PlanRow> rows)
        => rows.OrderByDescending(r => r.Hidden).ThenBy(r => r.Index).ThenBy(r => r.Id);

    // Photos: the hidden flag only, PhotoOrder keeps its value (no unique index); the cover is the visible photo with the lowest order.
    public static IReadOnlyList<PlanRowTarget> ChoosePhotos(IEnumerable<PlanRow> rows, int limit, bool hide)
    {
        var ranked = Rank(rows);
        var visible = VisibleCount(ranked.Count, limit, hide);
        return ranked.Select((r, i) => new PlanRowTarget(r.Id, i >= visible, r.Index)).ToList();
    }

    // Owner choice (V1 / V2) for indexed rows: the chosen rows are visible in their current relative order, the rest hidden.
    public static IReadOnlyList<PlanRowTarget> OwnerIndexed(IEnumerable<PlanRow> rows, IReadOnlySet<Guid> chosen)
    {
        var ranked = Rank(rows);
        return Number(ranked.Where(r => chosen.Contains(r.Id)), ranked.Where(r => !chosen.Contains(r.Id)));
    }

    // Owner choice for photos: the chosen ids are visible with PhotoOrder 0..n-1 in the given order (the first is the cover);
    // hidden photos follow with the next order values in their old relative order.
    public static IReadOnlyList<PlanRowTarget> OwnerPhotos(IEnumerable<PlanRow> rows, IReadOnlyList<Guid> chosenInOrder)
    {
        var ranked = Rank(rows);
        // The visible photos in the same order again: nothing moves (no write, no daily count).
        if (ranked.Where(r => !r.Hidden).Select(r => r.Id).SequenceEqual(chosenInOrder))
            return ranked.Select(r => new PlanRowTarget(r.Id, r.Hidden, r.Index)).ToList();
        var chosen = chosenInOrder.ToHashSet();
        var result = chosenInOrder.Select((id, i) => new PlanRowTarget(id, false, i)).ToList();
        result.AddRange(ranked.Where(r => !chosen.Contains(r.Id)).Select((r, i) => new PlanRowTarget(r.Id, true, chosenInOrder.Count + i)));
        return result;
    }

    // V1 / V2 request check: exactly min(limit, rows) distinct ids, all of them rows of this listing.
    public static bool ValidOwnerChoice(IReadOnlyCollection<Guid> rowIds, IReadOnlyList<Guid> chosen, int limit)
    {
        if (chosen.Count != Math.Min(Math.Max(0, limit), rowIds.Count)) return false;
        if (chosen.Distinct().Count() != chosen.Count) return false;
        var all = rowIds as IReadOnlySet<Guid> ?? rowIds.ToHashSet();
        return chosen.All(all.Contains);
    }

    // The rows whose target differs from where they are now (only these are written).
    public static IReadOnlyList<PlanRowTarget> Changed(IEnumerable<PlanRow> rows, IEnumerable<PlanRowTarget> targets)
    {
        var now = rows.ToDictionary(r => r.Id);
        return targets.Where(t => !now.TryGetValue(t.Id, out var r) || r.Hidden != t.Hidden || r.Index != t.Index).ToList();
    }

    public static int HiddenCount(IEnumerable<PlanRowTarget> targets) => targets.Count(t => t.Hidden);

    private static List<PlanRow> Rank(IEnumerable<PlanRow> rows)
        => rows.OrderBy(r => r.Hidden).ThenBy(r => r.Index).ThenBy(r => r.Id).ToList();

    private static List<PlanRowTarget> Number(IEnumerable<PlanRow> visible, IEnumerable<PlanRow> hidden)
    {
        var result = visible.Select((r, i) => new PlanRowTarget(r.Id, false, i + 1)).ToList();
        result.AddRange(hidden.Select((r, i) => new PlanRowTarget(r.Id, true, PlanRowBands.HiddenBase + i + 1)));
        return result;
    }
}

// Special prices (PausedByPlan, contract 3.4): the last added stay live. Active rules ranked by (PausedByPlan, CreatedAt DESC, Id);
// rank <= limit is live, so the owner's swap is never undone.
public readonly record struct PlanRuleRow(Guid Id, bool PausedByPlan, DateTime CreatedAt);

// Stay jobs (Active / Inactive, plan rule): of the owner's open jobs the newest `limit` by (CreatedAt DESC, Id) are Active, the
// rest Inactive; nothing goes Inactive while payment is OFF, and a new Plus makes them Active again. Returns Id -> inactive.
public readonly record struct PlanJobRow(Guid Id, bool Inactive, DateTime CreatedAt);

public static class PlanJobChoice
{
    public static IReadOnlyDictionary<Guid, bool> Inactive(IEnumerable<PlanJobRow> openJobs, int limit, bool hide)
    {
        var ranked = openJobs.OrderByDescending(j => j.CreatedAt).ThenBy(j => j.Id).ToList();
        var active = PlanRowChoice.VisibleCount(ranked.Count, limit, hide);
        return ranked.Select((j, i) => (j.Id, Inactive: i >= active)).ToDictionary(x => x.Id, x => x.Inactive);
    }
}

public static class PlanRuleChoice
{
    public static IReadOnlyDictionary<Guid, bool> PausedByPlan(IEnumerable<PlanRuleRow> activeRules, int limit, bool hide)
    {
        var ranked = activeRules.OrderBy(r => r.PausedByPlan).ThenByDescending(r => r.CreatedAt).ThenBy(r => r.Id).ToList();
        var live = PlanRowChoice.VisibleCount(ranked.Count, limit, hide);
        return ranked.Select((r, i) => (r.Id, Paused: i >= live)).ToDictionary(x => x.Id, x => x.Paused);
    }
}
