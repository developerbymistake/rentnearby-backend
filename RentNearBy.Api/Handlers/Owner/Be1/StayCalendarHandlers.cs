using System.Security.Claims;
using FluentValidation;
using RentNearBy.Core.DTOs.Owner;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Services.Owner.Be1;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers.Owner.Be1;

// Stay Owner calendar C1 to C11 under /api/v1/stays/me/calendar (contract 2.1). Not tab-gated: the owner's own calendar and notes
// stay usable while the public Stays tab is off (UC-S-13, UC-S-14). Thin: caller, rate limit, owner context, service, mapping.
public static class StayCalendarHandlers
{
    private const string Kind = ListingKinds.Stay;

    public static async Task<IResult> Month(
        DateOnly? from, DateOnly? to, ClaimsPrincipal principal, OwnerCalendarReader reader, IRateLimitService limiter, HttpContext http, CancellationToken ct)
    {
        if (!OwnerCalendarHttp.TryCaller(principal, out var me)) return UnauthorizedResponse();
        if (await OwnerHttp.ReadLimitAsync(limiter, http, OwnerRateLimitKeys.Read(me)) is { } denied) return denied;
        var (ctx, fail) = await OwnerCalendarHttp.ContextAsync(me, Kind, reader, ct);
        if (ctx == null) return fail!;
        var start = from ?? ctx.Window.MonthFrom;
        var end = to ?? (from is { } f ? f : new DateOnly(ctx.Today.Year, ctx.Today.Month, 1).AddMonths(1).AddDays(-1));
        var result = await reader.MonthAsync(ctx, start, end, ct);
        if (!result.Ok) return OwnerCalendarHttp.Error(result.Error!, StayCalendarEntryDto.From);
        var m = result.Value!;
        return OkResponse(new StayCalendarMonthDto(
            StayCalendarEntryDto.From(m.Entries),
            m.Blocks.Select(StayBlockedRangeHandlers.ToDto).ToList(),
            m.SpecialPrices.Select(p => new StayCalendarSpecialPriceDto(p.Id, p.DateFrom, p.DateTo, p.Direction, p.Percent, p.Label, p.IsPaused, p.PausedByPlan)).ToList(),
            m.Festivals.Select(x => new CalendarFestivalDto(x.Id, x.Name, x.DateFrom, x.DateTo)).ToList(),
            m.Meters, m.Window, m.HiddenCount, m.Reminder, m.SummaryAllowed, m.PaymentEnabled, m.Today));
    }

    public static async Task<IResult> Upcoming(
        string? cursor, ClaimsPrincipal principal, OwnerCalendarReader reader, IRateLimitService limiter, HttpContext http, CancellationToken ct)
    {
        if (!OwnerCalendarHttp.TryCaller(principal, out var me)) return UnauthorizedResponse();
        if (!OwnerHttp.TryDateCursor(cursor, out var key)) return OwnerHttp.BadCursor();
        if (await OwnerHttp.ReadLimitAsync(limiter, http, OwnerRateLimitKeys.Read(me)) is { } denied) return denied;
        var (ctx, fail) = await OwnerCalendarHttp.ContextAsync(me, Kind, reader, ct);
        if (ctx == null) return fail!;
        var page = await reader.UpcomingAsync(ctx, key, ct);
        return OkResponse(new StayCalendarUpcomingDto(StayCalendarEntryDto.From(page.Items), page.NextCursor, page.HiddenCount, page.BeyondWindowAppCount, page.BeyondWindowFirstDate));
    }

    public static async Task<IResult> Past(
        string? cursor, ClaimsPrincipal principal, OwnerCalendarReader reader, IRateLimitService limiter, HttpContext http, CancellationToken ct)
    {
        if (!OwnerCalendarHttp.TryCaller(principal, out var me)) return UnauthorizedResponse();
        if (!OwnerHttp.TryDateCursor(cursor, out var key)) return OwnerHttp.BadCursor();
        if (await OwnerHttp.ReadLimitAsync(limiter, http, OwnerRateLimitKeys.Read(me)) is { } denied) return denied;
        var (ctx, fail) = await OwnerCalendarHttp.ContextAsync(me, Kind, reader, ct);
        if (ctx == null) return fail!;
        var page = await reader.PastAsync(ctx, key, ct);
        return OkResponse(new OwnerPageDto<StayCalendarEntryDto>(StayCalendarEntryDto.From(page.Items), page.NextCursor));
    }

    public static async Task<IResult> Entry(
        Guid id, ClaimsPrincipal principal, OwnerCalendarReader reader, IRateLimitService limiter, HttpContext http, CancellationToken ct)
    {
        if (!OwnerCalendarHttp.TryCaller(principal, out var me)) return UnauthorizedResponse();
        if (await OwnerHttp.ReadLimitAsync(limiter, http, OwnerRateLimitKeys.Read(me)) is { } denied) return denied;
        var (ctx, fail) = await OwnerCalendarHttp.ContextAsync(me, Kind, reader, ct);
        if (ctx == null) return fail!;
        var row = await reader.EntryAsync(ctx, id, ct);
        return row == null ? NotFoundResponse("Entry not found.") : OkResponse(StayCalendarEntryDto.From(row));
    }

    public static async Task<IResult> Create(
        StayCalendarEntryRequest request, IValidator<StayCalendarEntryRequest> validator, ClaimsPrincipal principal,
        OwnerCalendarReader reader, OwnerCalendarWriter writer, IRateLimitService limiter, HttpContext http, CancellationToken ct)
    {
        if (!OwnerCalendarHttp.TryCaller(principal, out var me)) return UnauthorizedResponse();
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid) return OwnerHttp.Invalid(validation);
        return await OwnerHttp.WriteLimitAsync(limiter, http, OwnerRateLimitKeys.Write(me), async () =>
        {
            var (ctx, fail) = await OwnerCalendarHttp.ContextAsync(me, Kind, reader, ct);
            if (ctx == null) return fail!;
            var result = await writer.CreateAsync(ctx, request.Id, Input(request), ct);
            return result.Ok ? OkResponse(Write(result.Value!)) : OwnerCalendarHttp.Error(result.Error!, StayCalendarEntryDto.From);
        });
    }

    public static async Task<IResult> Update(
        Guid id, StayCalendarEntryRequest request, IValidator<StayCalendarEntryRequest> validator, ClaimsPrincipal principal,
        OwnerCalendarReader reader, OwnerCalendarWriter writer, IRateLimitService limiter, HttpContext http, CancellationToken ct)
    {
        if (!OwnerCalendarHttp.TryCaller(principal, out var me)) return UnauthorizedResponse();
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid) return OwnerHttp.Invalid(validation);
        return await OwnerHttp.WriteLimitAsync(limiter, http, OwnerRateLimitKeys.Write(me), async () =>
        {
            var (ctx, fail) = await OwnerCalendarHttp.ContextAsync(me, Kind, reader, ct);
            if (ctx == null) return fail!;
            var result = await writer.UpdateAsync(ctx, id, request.Version, Input(request), ct);
            return result.Ok ? OkResponse(Write(result.Value!)) : OwnerCalendarHttp.Error(result.Error!, StayCalendarEntryDto.From);
        });
    }

    public static async Task<IResult> Delete(
        Guid id, ClaimsPrincipal principal, OwnerCalendarReader reader, OwnerCalendarWriter writer, IRateLimitService limiter, HttpContext http, CancellationToken ct)
    {
        if (!OwnerCalendarHttp.TryCaller(principal, out var me)) return UnauthorizedResponse();
        return await OwnerHttp.WriteLimitAsync(limiter, http, OwnerRateLimitKeys.Write(me), async () =>
        {
            var (ctx, fail) = await OwnerCalendarHttp.ContextAsync(me, Kind, reader, ct);
            if (ctx == null) return fail!;
            var error = await writer.DeleteAsync(ctx, id, ct);
            return error == null ? NoContentResponse() : OwnerCalendarHttp.Error(error, StayCalendarEntryDto.From);
        });
    }

    public static async Task<IResult> Money(
        Guid id, StayMoneyNoteRequest request, ClaimsPrincipal principal,
        OwnerCalendarReader reader, MoneyNoteService money, IRateLimitService limiter, HttpContext http, CancellationToken ct)
    {
        if (!OwnerCalendarHttp.TryCaller(principal, out var me)) return UnauthorizedResponse();
        return await OwnerHttp.WriteLimitAsync(limiter, http, OwnerRateLimitKeys.Write(me), async () =>
        {
            var (ctx, fail) = await OwnerCalendarHttp.ContextAsync(me, Kind, reader, ct);
            if (ctx == null) return fail!;
            var result = await money.SetAsync(ctx, id, request.Version, OwnerCalendarHttp.Money(request.FinalAmount, request.AdvanceReceived, request.PaidInFull), ct);
            return result.Ok ? OkResponse(StayCalendarEntryDto.From(result.Value!)) : OwnerCalendarHttp.Error(result.Error!, StayCalendarEntryDto.From);
        });
    }

    public static async Task<IResult> Summary(
        string? month, ClaimsPrincipal principal, OwnerCalendarReader reader, IRateLimitService limiter, HttpContext http, CancellationToken ct)
    {
        if (!OwnerCalendarHttp.TryCaller(principal, out var me)) return UnauthorizedResponse();
        if (await OwnerHttp.ReadLimitAsync(limiter, http, OwnerRateLimitKeys.Read(me)) is { } denied) return denied;
        var (ctx, fail) = await OwnerCalendarHttp.ContextAsync(me, Kind, reader, ct);
        if (ctx == null) return fail!;
        var result = await reader.SummaryAsync(ctx, month, ct);
        if (!result.Ok) return OwnerCalendarHttp.Error(result.Error!, StayCalendarEntryDto.From);
        var s = result.Value!;
        return OkResponse(new StayMoneySummaryDto(s.Month, s.FinalTotal, s.ReceivedTotal, s.BalanceTotal, s.EntryCount, s.DueCount,
            StayCalendarEntryDto.From(s.TopBalances), s.PaymentEnabled));
    }

    public static async Task<IResult> BalanceDue(
        string? month, string? cursor, ClaimsPrincipal principal, OwnerCalendarReader reader, IRateLimitService limiter, HttpContext http, CancellationToken ct)
    {
        if (!OwnerCalendarHttp.TryCaller(principal, out var me)) return UnauthorizedResponse();
        if (!OwnerHttp.TryNumberCursor(cursor, out var key)) return OwnerHttp.BadCursor();
        if (await OwnerHttp.ReadLimitAsync(limiter, http, OwnerRateLimitKeys.Read(me)) is { } denied) return denied;
        var (ctx, fail) = await OwnerCalendarHttp.ContextAsync(me, Kind, reader, ct);
        if (ctx == null) return fail!;
        var result = await reader.BalanceDueAsync(ctx, month, key, ct);
        if (!result.Ok) return OwnerCalendarHttp.Error(result.Error!, StayCalendarEntryDto.From);
        return OkResponse(new OwnerPageDto<StayCalendarEntryDto>(StayCalendarEntryDto.From(result.Value!.Items), result.Value.NextCursor));
    }

    public static async Task<IResult> Reminder(
        StayReminderRequest request, ClaimsPrincipal principal, OwnerCalendarReader reader, OwnerCalendarWriter writer,
        IRateLimitService limiter, HttpContext http, CancellationToken ct)
    {
        if (!OwnerCalendarHttp.TryCaller(principal, out var me)) return UnauthorizedResponse();
        return await OwnerHttp.WriteLimitAsync(limiter, http, OwnerRateLimitKeys.Write(me), async () =>
        {
            var (ctx, fail) = await OwnerCalendarHttp.ContextAsync(me, Kind, reader, ct);
            if (ctx == null) return fail!;
            var result = await writer.SetReminderAsync(ctx, request.On, ct);
            return result.Ok ? OkResponse(new { reminder = result.Value! }) : OwnerCalendarHttp.Error(result.Error!, StayCalendarEntryDto.From);
        });
    }

    public static ManualEntryInput Input(StayCalendarEntryRequest r) => new(
        r.Label?.Trim() ?? string.Empty,
        r.DateFrom ?? default,
        r.DateTo ?? default,
        null, null, null,
        OwnerCalendarRules.Clean(r.Note),
        OwnerCalendarHttp.Money(r.FinalAmount, r.AdvanceReceived, r.PaidInFull));

    private static StayCalendarEntryWriteDto Write(CalendarEntryWrite w) => new(StayCalendarEntryDto.From(w.Entry), w.Meters, w.Overlap);
}
