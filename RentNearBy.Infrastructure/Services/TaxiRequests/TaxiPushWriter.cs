using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Entities.TaxiRequests;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services.TaxiRequests;

// Adds rows to the caller's context and never saves: the caller's transaction commits them with the state change.
// After that commit the caller wakes the outbox relay.
public sealed class TaxiPushWriter(TaxiAckTokens ackTokens, TimeProvider? clock = null) : ITaxiPushWriter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan RingEndTtl = TimeSpan.FromSeconds(60);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task RingAsync(ApplicationDbContext db, RingPush ring, CancellationToken ct = default)
    {
        var request = await FindRequestAsync(db, ring.RequestId, ct)
            ?? throw new InvalidOperationException($"taxi ring: request {ring.RequestId} not found");
        var now = _clock.GetUtcNow().UtcDateTime;
        var data = TaxiRingPayload.Build(
            ring.OfferId, ring.RequestId, ring.ExpiresAtUtc, now, 0, ackTokens.Create(ring.OfferId), SnapshotOf(request), ring.DriverUserId);

        AddOutbox(db, new OutboxPushPayload(
            TaxiPushKinds.RingKey(ring.OfferId), ring.DriverUserId, null,
            TaxiRingPayload.RingTitle, data["line1"], data["action_route"], data["action_args_json"], TaxiPushKinds.RingTag(ring.OfferId),
            0, NotificationTypes.TaxiRequestRing, ring.ExpiresAtUtc, data), now);
    }

    public Task RingEndAsync(ApplicationDbContext db, RingEndPush ringEnd, CancellationToken ct = default)
    {
        var parts = TaxiRingPayload.BuildEnd(ringEnd.OfferId, ringEnd.RequestId, ringEnd.Reason);
        if (parts is null) return Task.CompletedTask;
        var now = _clock.GetUtcNow().UtcDateTime;

        AddOutbox(db, new OutboxPushPayload(
            TaxiPushKinds.RingEndKey(ringEnd.OfferId), ringEnd.DriverUserId, null,
            parts.Title, parts.Body, parts.Data["action_route"], parts.Data["action_args_json"], TaxiPushKinds.RingTag(ringEnd.OfferId),
            0, NotificationTypes.TaxiRequestRingEnded, now + RingEndTtl, parts.Data, ringEnd.ExceptToken), now);
        return Task.CompletedTask;
    }

    public async Task NotifyAsync(ApplicationDbContext db, TaxiNotice notice, CancellationToken ct = default)
        => await NotifyAsync(db, notice, await DetailFromRequestAsync(db, notice, ct), ct);

    // The detail picks the copy variant (end reason, during the trip, driver side).
    public Task NotifyAsync(ApplicationDbContext db, TaxiNotice notice, TaxiNoticeDetail? detail, CancellationToken ct = default)
    {
        var copy = TaxiRequestNotifications.Build(
            TaxiNoticeTypes.TypeOf(notice.Kind), notice.TripId, notice.RequestId, notice.OfferId, notice.PersonName, detail);
        var now = _clock.GetUtcNow().UtcDateTime;
        var ev = new NotificationEvent
        {
            Id = Guid.NewGuid(),
            TargetUserId = notice.UserId,
            Type = copy.Type,
            Title = copy.Title,
            Body = copy.Body,
            ActionRoute = copy.Route,
            ActionArgumentsJson = copy.ArgsJson,
            CreatedAt = now,
        };
        db.NotificationEvents.Add(ev);
        AddOutbox(db, new OutboxPushPayload(
            $"ev:{ev.Id:N}", notice.UserId, ev.Id, ev.Title, ev.Body, ev.ActionRoute, ev.ActionArgumentsJson, $"n:{ev.Id:N}"), now);
        return Task.CompletedTask;
    }

    private static void AddOutbox(ApplicationDbContext db, OutboxPushPayload push, DateTime now)
        => db.OutboxMessages.Add(new OutboxMessage
        {
            Key = push.Key,
            Exchange = OutboxTargets.PushExchange,
            RoutingKey = OutboxTargets.PushRoutingKey,
            Payload = JsonSerializer.Serialize(push, Json),
            CreatedAt = now,
            NextAttemptAt = now,
        });

    private static async Task<TaxiRequest?> FindRequestAsync(ApplicationDbContext db, Guid requestId, CancellationToken ct)
        => db.Set<TaxiRequest>().Local.FirstOrDefault(r => r.Id == requestId)
            ?? await db.Set<TaxiRequest>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == requestId, ct);

    internal sealed record NoticeFacts(Guid TravellerUserId, string? EndReason);

    private static async Task<TaxiNoticeDetail?> DetailFromRequestAsync(ApplicationDbContext db, TaxiNotice notice, CancellationToken ct)
    {
        if (notice.Kind == TaxiNoticeKind.ChooseDriver) return new TaxiNoticeDetail(Count: notice.Count);
        if (notice.Kind == TaxiNoticeKind.SelectionLapsed) return new TaxiNoticeDetail(Reason: notice.Reason);
        if (notice.Kind is not (TaxiNoticeKind.RequestEnded or TaxiNoticeKind.RequestClosed or TaxiNoticeKind.DriverWithdrew or TaxiNoticeKind.TripEnded)) return null;
        var facts = await FindFactsAsync(db, notice.RequestId, ct)
            ?? throw new InvalidOperationException($"taxi notice: request {notice.RequestId} not found");
        return DetailOf(notice, facts);
    }

    // Transitions are conditional SQL, so a tracked row can hold an old EndReason: only an unsaved row is read from the tracker.
    private static async Task<NoticeFacts?> FindFactsAsync(ApplicationDbContext db, Guid requestId, CancellationToken ct)
    {
        var added = db.ChangeTracker.Entries<TaxiRequest>().FirstOrDefault(e => e.State == EntityState.Added && e.Entity.Id == requestId)?.Entity;
        if (added is not null) return new NoticeFacts(added.TravellerUserId, added.EndReason);
        return await db.Set<TaxiRequest>().AsNoTracking().Where(r => r.Id == requestId)
            .Select(r => new NoticeFacts(r.TravellerUserId, r.EndReason)).FirstOrDefaultAsync(ct);
    }

    internal static TaxiNoticeDetail? DetailOf(TaxiNotice notice, NoticeFacts facts) => notice.Kind switch
    {
        TaxiNoticeKind.RequestEnded or TaxiNoticeKind.RequestClosed => new TaxiNoticeDetail(Reason: facts.EndReason),
        TaxiNoticeKind.DriverWithdrew => new TaxiNoticeDetail(DuringTrip: facts.EndReason == RequestEndReasons.DriverLeftTrip),
        TaxiNoticeKind.TripEnded => new TaxiNoticeDetail(ToDriver: notice.UserId != facts.TravellerUserId),
        _ => null,
    };

    // Cities come from the stored trip snapshot: "cities" (objects with "name", or strings) or "cityNames"; keys match in any case.
    internal static TaxiRingSnapshot SnapshotOf(TaxiRequest r)
        => new(r.DateFrom, r.DateTo, r.PickupCityName, CityNames(r.TripSnapshotJson), r.WantPickup, r.WantSightseeing, r.WantDrop,
            CountItems(r.SightseeingDaysJson), r.Seats, r.Travellers);

    internal static IReadOnlyList<string> CityNames(string? snapshotJson)
    {
        if (string.IsNullOrWhiteSpace(snapshotJson)) return [];
        try
        {
            using var doc = JsonDocument.Parse(snapshotJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return [];
            foreach (var key in new[] { "cities", "cityNames" })
            {
                if (Property(doc.RootElement, key) is not { ValueKind: JsonValueKind.Array } arr) continue;
                var names = new List<string>();
                foreach (var item in arr.EnumerateArray())
                {
                    var name = item.ValueKind == JsonValueKind.String ? item.GetString()
                        : item.ValueKind == JsonValueKind.Object && Property(item, "name") is { ValueKind: JsonValueKind.String } n ? n.GetString()
                        : null;
                    if (!string.IsNullOrWhiteSpace(name)) names.Add(name.Trim());
                }
                return names;
            }
        }
        catch (JsonException) { }
        return [];
    }

    private static JsonElement? Property(JsonElement obj, string name)
    {
        foreach (var p in obj.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }

    private static int CountItems(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return 0;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.GetArrayLength() : 0;
        }
        catch (JsonException) { return 0; }
    }
}
