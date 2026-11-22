using RentNearBy.Core.DTOs.Owner;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Interfaces.Owner;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services.Owner.Be2;

// T1: the one dated-price pass of every stay list read, pure over the rules and blocks riding on the cached rows (no query).
// Always strips the cache-only rows; without checkIn nothing else changes. Order and MinPricePerNight never change.
public sealed class StayDatedPricing : IStayDatedPricing
{
    public List<StayNearbyDto> Apply(
        List<StayNearbyDto> items, DateOnly? checkIn, int nights, int? guests, int? rooms, bool groundFloorOnly, DateOnly todayIst)
    {
        foreach (var item in items)
        {
            var rules = StaySpecialPriceRules.ActiveRules(item.Rules, todayIst);
            var blocks = StaySpecialPriceRules.ActiveBlocks(item.Blocks, todayIst);
            item.Rules = null;
            item.Blocks = null;
            if (checkIn is { } from) Dated(item, from, Math.Max(1, nights), rules, blocks);
        }
        return items;
    }

    public static void Dated(StayNearbyDto item, DateOnly checkIn, int nights, IReadOnlyList<PriceRule> rules, IReadOnlyList<(DateOnly From, DateOnly To)> blocks)
    {
        item.FullOnDates = StaySpecialPriceMath.TouchesBlock(checkIn, nights, blocks);
        var percent = StaySpecialPriceMath.LowerPercentFor(checkIn, nights, rules);
        item.PriceChip = PriceChipDto.Of(percent);
        item.SetOffer(null);

        // The party quote (StayNearbyRooms.Apply) when it fits: every line takes its price for the dates.
        if (item.Fits == true && item.QuoteLines is { Count: > 0 } lines && lines.All(l => l.PricePerNight is > 0))
        {
            long total = 0;
            foreach (var line in lines)
            {
                var lineTotal = StaySpecialPriceMath.Total(line.PricePerNight!.Value, checkIn, nights, rules);
                total += (long)line.Units * lineTotal;
                line.PricePerNight = StaySpecialPriceMath.PerNight(lineTotal, nights);
            }
            var datesTotal = (int)Math.Min(total, int.MaxValue);
            item.BasePerNight = item.QuotePerNight;
            item.QuoteTotal = datesTotal;
            item.QuotePerNight = StaySpecialPriceMath.PerNight(datesTotal, nights);
            item.DatesTotal = datesTotal;
            item.DatesPerNight = item.QuotePerNight;
            item.SetOffer(StayOfferFields.Dated(item.BasePerNight, item.DatesPerNight, percent));
            return;
        }

        // No party (or it does not fit): the cheapest visible room, the FROM price of the card.
        var cheapest = item.Rooms?.Where(r => r.PricePerNight is > 0).Select(r => r.PricePerNight!.Value).DefaultIfEmpty().Min() ?? 0;
        if (cheapest <= 0) return;
        var single = StaySpecialPriceMath.Total(cheapest, checkIn, nights, rules);
        item.BasePerNight = cheapest;
        item.DatesTotal = single;
        item.DatesPerNight = StaySpecialPriceMath.PerNight(single, nights);
        item.SetOffer(StayOfferFields.Dated(item.BasePerNight, item.DatesPerNight, percent));
    }
}

// T2: stay detail and share link. One statement (rules and blocks of the stay) only when checkIn is given.
public sealed class StayDetailPricing(ApplicationDbContext db) : IStayDetailPricing
{
    public async Task ApplyAsync(StayPublicDto dto, DateOnly? checkIn, int nights, DateOnly todayIst, CancellationToken ct = default)
    {
        if (checkIn is not { } from) return;
        var facts = await LoadAsync(dto.Id, todayIst, ct);
        var (min, chip, full) = Fill(dto.Rooms, from, Math.Max(1, nights), facts);
        dto.DatesFromPerNight = min;
        dto.PriceChip = chip;
        dto.FullOnDates = full;
    }

    public async Task ApplyAsync(StayPublicSlugDto dto, DateOnly? checkIn, int nights, DateOnly todayIst, CancellationToken ct = default)
    {
        if (checkIn is not { } from) return;
        var facts = await LoadAsync(dto.Id, todayIst, ct);
        var (min, chip, full) = Fill(dto.Rooms, from, Math.Max(1, nights), facts);
        dto.DatesFromPerNight = min;
        dto.PriceChip = chip;
        dto.FullOnDates = full;
    }

    private async Task<StayPriceFacts.StayPriceFactSet?> LoadAsync(Guid stayId, DateOnly today, CancellationToken ct)
        => (await StayPriceFacts.LoadAsync(db, [stayId], today, ct)).GetValueOrDefault(stayId);

    public static (int? MinPerNight, PriceChipDto? Chip, bool Full) Fill(
        IReadOnlyList<StayRoomDto> rooms, DateOnly checkIn, int nights, StayPriceFacts.StayPriceFactSet? facts)
    {
        var rules = facts?.PlainRules ?? [];
        var blocks = facts?.Blocks ?? [];
        int? min = null;
        foreach (var room in rooms)
        {
            if (room.PricePerNight <= 0) continue;
            room.BasePrice = room.PricePerNight;
            room.PriceForDates = StaySpecialPriceMath.PerNight(StaySpecialPriceMath.Total(room.PricePerNight, checkIn, nights, rules), nights);
            min = min is { } m ? Math.Min(m, room.PriceForDates.Value) : room.PriceForDates;
        }
        return (min, PriceChipDto.Of(StaySpecialPriceMath.LowerPercentFor(checkIn, nights, rules)), StaySpecialPriceMath.TouchesBlock(checkIn, nights, blocks));
    }
}
