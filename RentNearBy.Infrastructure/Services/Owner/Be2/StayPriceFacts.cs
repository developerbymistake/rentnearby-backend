using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.DTOs.Owner;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services.Owner.Be2;

// The price facts of given stays in ONE statement (contract 2.5 T2 / T4): visible rooms, live active rules (with the owner-side
// label) and active blocks, each as a json_agg column. Also the parsers of the cache-only rows of the nearby statements (8.3).
public static class StayPriceFacts
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public sealed record StayPriceFactSet(Guid StayId, IReadOnlyList<ListedRoom> Rooms, IReadOnlyList<LabelledRule> Rules, IReadOnlyList<(DateOnly From, DateOnly To)> Blocks)
    {
        public List<PriceRule> PlainRules => Rules.Select(r => r.Rule).ToList();
    }

    public sealed class StayPriceFactsRow
    {
        public Guid StayId { get; set; }
        public string? RoomsJson { get; set; }
        public string? RulesJson { get; set; }
        public string? BlocksJson { get; set; }
    }

    private sealed class StayPriceRoomJson
    {
        public string RoomName { get; set; } = string.Empty;
        public int PricePerNight { get; set; }
        public int MaxGuests { get; set; }
        public short? Floor { get; set; }
    }

    private sealed class StayPriceRuleJson
    {
        public DateOnly From { get; set; }
        public DateOnly To { get; set; }
        public bool Lower { get; set; }
        public int Percent { get; set; }
        public string? Label { get; set; }
    }

    public const string Sql = """
        SELECT s."Id" AS "StayId",
               (SELECT json_agg(json_build_object('roomName', r."RoomName", 'pricePerNight', r."PricePerNight", 'maxGuests', r."MaxGuests", 'floor', r."Floor")
                                ORDER BY r."RoomIndex")
                FROM "StayRooms" r WHERE r."StayListingId" = s."Id" AND NOT r."HiddenByPlan")::text AS "RoomsJson",
               (SELECT json_agg(json_build_object('from', p."DateFrom", 'to', p."DateTo", 'lower', p."Direction" = 'Lower', 'percent', p."Percent", 'label', p."LabelCopy")
                                ORDER BY p."DateFrom")
                FROM "StaySpecialPrices" p
                WHERE p."StayListingId" = s."Id" AND p."DateTo" >= @today AND NOT p."IsPaused" AND NOT p."PausedByPlan")::text AS "RulesJson",
               (SELECT json_agg(json_build_object('from', b."DateFrom", 'to', b."DateTo") ORDER BY b."DateFrom")
                FROM "StayBlockedRanges" b WHERE b."StayListingId" = s."Id" AND b."DateTo" >= @today)::text AS "BlocksJson"
        FROM "StayListings" s
        WHERE s."Id" = ANY(@ids)
        """;

    public static async Task<Dictionary<Guid, StayPriceFactSet>> LoadAsync(
        ApplicationDbContext db, IReadOnlyCollection<Guid> stayIds, DateOnly today, CancellationToken ct = default)
    {
        if (stayIds.Count == 0) return [];
        var rows = await db.Database.SqlQueryRaw<StayPriceFactsRow>(Sql,
                new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = stayIds.Distinct().ToArray() },
                new NpgsqlParameter("today", NpgsqlDbType.Date) { Value = today })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.StayId, ToFacts);
    }

    public static StayPriceFactSet ToFacts(StayPriceFactsRow row)
    {
        var rooms = Parse<StayPriceRoomJson>(row.RoomsJson).Select(r => new ListedRoom(r.RoomName, r.PricePerNight, r.MaxGuests, r.Floor)).ToList();
        var rules = Parse<StayPriceRuleJson>(row.RulesJson)
            .Where(r => r.To >= r.From)
            .Select(r => new LabelledRule(new PriceRule(r.From, r.To, r.Lower, r.Percent), r.Label)).ToList();
        var blocks = Parse<StayBlockRow>(row.BlocksJson).Where(b => b.To >= b.From).Select(b => (b.From, b.To)).ToList();
        return new StayPriceFactSet(row.StayId, rooms, rules, blocks);
    }

    // A read that returns candidate rows without the dated-price pass drops the cache-only rows (no response holds them).
    public static List<StayNearbyDto> Strip(List<StayNearbyDto> items)
    {
        foreach (var item in items)
        {
            item.Rules = null;
            item.Blocks = null;
        }
        return items;
    }

    // Nearby statements: rules / blocks as their json_agg text. Bad or empty JSON = none (a broken row never fails a list read).
    public static List<StayRuleRow>? ParseRules(string? json) => NullIfEmpty(Parse<StayRuleRow>(json));

    public static List<StayBlockRow>? ParseBlocks(string? json) => NullIfEmpty(Parse<StayBlockRow>(json));

    private static List<T>? NullIfEmpty<T>(List<T> list) => list.Count == 0 ? null : list;

    private static List<T> Parse<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<T>>(json, Json) ?? []; }
        catch (JsonException) { return []; }
        catch (NotSupportedException) { return []; }
    }
}
