using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Owner;
using RentNearBy.Core.Entities.Owner;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services.Owner.Be2;

// F1 (any signed-in user) and A2 (admin). Festival rows only feed owner chips and reminders; saving never touches an owner's rule.
public sealed class FestivalService(ApplicationDbContext db, FestivalCache cache, TimeProvider clock)
{
    public const int MaxRangeDays = FestivalCache.AheadDays + FestivalCache.BackDays;

    private DateOnly Today => IstTime.TodayIst(clock.GetUtcNow().UtcDateTime);

    // F1: the cached window answers with 0 statements; a range outside it is one direct statement.
    public async Task<Be2Result<FestivalListDto>> ListAsync(string? stateName, DateOnly? from, DateOnly? to, CancellationToken ct = default)
    {
        var today = Today;
        var (windowFrom, windowTo) = FestivalCache.Window(today);
        var f = from ?? today;
        var t = to ?? windowTo;
        if (t < f || t.DayNumber - f.DayNumber > MaxRangeDays) return Be2Result<FestivalListDto>.Fail(OwnerErrorTypes.BadRequest, "Pick a shorter date range.");
        var state = await cache.KnownStateAsync(db, stateName, ct);
        if (f >= windowFrom && t <= windowTo)
            return new(new FestivalListDto(FestivalCache.Slice(await cache.GetAsync(db, state, today, ct), f, t)));
        return new(new FestivalListDto(await FestivalCache.Query(db, state, f, t).ToListAsync(ct)));
    }

    // A2 list: (DateFrom DESC, Id) keyset over ix_festivaldates_from_id, 20 a page.
    public async Task<OwnerPageDto<AdminFestivalDto>> PageAsync((DateOnly Date, Guid Id)? after, CancellationToken ct = default)
    {
        var q = db.Set<FestivalDate>().AsNoTracking();
        if (after is { } a)
            q = q.Where(x => x.DateFrom < a.Date || (x.DateFrom == a.Date && x.Id.CompareTo(a.Id) > 0));
        var rows = await q.OrderByDescending(x => x.DateFrom).ThenBy(x => x.Id)
            .Take(OwnerLimits.PageSize + 1)
            .Select(x => new AdminFestivalDto(x.Id, x.Name, x.DateFrom, x.DateTo, x.StateName, x.IsActive, x.CreatedAt, x.UpdatedAt))
            .ToListAsync(ct);
        var more = rows.Count > OwnerLimits.PageSize;
        var page = more ? rows.Take(OwnerLimits.PageSize).ToList() : rows;
        return new OwnerPageDto<AdminFestivalDto>(page, more ? OwnerCursors.EncodeDate(page[^1].DateFrom, page[^1].Id) : null);
    }

    public async Task<AdminFestivalStatesDto> StatesAsync(CancellationToken ct = default)
        => new(await db.Districts.AsNoTracking()
            .Where(d => d.StateName != null && d.StateName != "")
            .Select(d => d.StateName!)
            .Distinct()
            .OrderBy(s => s)
            .ToListAsync(ct));

    // POST with a client id: ON CONFLICT ("Id") DO NOTHING, a replay answers the stored row.
    public async Task<Be2Result<AdminFestivalDto>> CreateAsync(AdminFestivalRequest request, CancellationToken ct = default)
    {
        var state = Normalise(request.StateName);
        if (state != null && !await StateExistsAsync(state, ct)) return Be2Result<AdminFestivalDto>.Fail(OwnerErrorTypes.BadRequest, "Pick a state from the list.");
        var name = request.Name.Trim();
        var rows = await db.Database.SqlQuery<FestivalRow>($"""
            INSERT INTO "FestivalDates" ("Id", "Name", "DateFrom", "DateTo", "StateName", "IsActive", "CreatedAt", "UpdatedAt")
            VALUES ({request.Id}, {name}, {request.DateFrom}, {request.DateTo}, {state}, {request.IsActive}, now(), now())
            ON CONFLICT ("Id") DO NOTHING
            RETURNING "Id", "Name", "DateFrom", "DateTo", "StateName", "IsActive", "CreatedAt", "UpdatedAt"
            """).ToListAsync(ct);
        if (rows.Count == 1)
        {
            cache.Drop();
            return new(rows[0].ToDto(), Created: true);
        }
        var stored = await db.Set<FestivalDate>().AsNoTracking().Where(x => x.Id == request.Id)
            .Select(x => new AdminFestivalDto(x.Id, x.Name, x.DateFrom, x.DateTo, x.StateName, x.IsActive, x.CreatedAt, x.UpdatedAt))
            .FirstOrDefaultAsync(ct);
        return stored == null ? Be2Result<AdminFestivalDto>.Fail(OwnerErrorTypes.IdTaken) : new(stored, Replay: true);
    }

    public async Task<Be2Result<AdminFestivalDto>> UpdateAsync(Guid id, AdminFestivalRequest request, CancellationToken ct = default)
    {
        var state = Normalise(request.StateName);
        if (state != null && !await StateExistsAsync(state, ct)) return Be2Result<AdminFestivalDto>.Fail(OwnerErrorTypes.BadRequest, "Pick a state from the list.");
        var name = request.Name.Trim();
        var rows = await db.Database.SqlQuery<FestivalRow>($"""
            UPDATE "FestivalDates"
            SET "Name" = {name}, "DateFrom" = {request.DateFrom}, "DateTo" = {request.DateTo}, "StateName" = {state},
                "IsActive" = {request.IsActive}, "UpdatedAt" = now()
            WHERE "Id" = {id}
            RETURNING "Id", "Name", "DateFrom", "DateTo", "StateName", "IsActive", "CreatedAt", "UpdatedAt"
            """).ToListAsync(ct);
        if (rows.Count == 0) return Be2Result<AdminFestivalDto>.Missing("Festival not found");
        cache.Drop();
        return new(rows[0].ToDto());
    }

    // Idempotent; reminder marks of the festival go with it (FK cascade).
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var n = await db.Set<FestivalDate>().Where(x => x.Id == id).ExecuteDeleteAsync(ct);
        if (n > 0) cache.Drop();
    }

    public static string? Normalise(string? stateName) => string.IsNullOrWhiteSpace(stateName) ? null : stateName.Trim();

    private Task<bool> StateExistsAsync(string state, CancellationToken ct)
        => db.Districts.AsNoTracking().AnyAsync(d => d.StateName == state, ct);

    public sealed class FestivalRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public DateOnly DateFrom { get; set; }
        public DateOnly DateTo { get; set; }
        public string? StateName { get; set; }
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        public AdminFestivalDto ToDto() => new(Id, Name, DateFrom, DateTo, StateName, IsActive, CreatedAt, UpdatedAt);
    }
}
