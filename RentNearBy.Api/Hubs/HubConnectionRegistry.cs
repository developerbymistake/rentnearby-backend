using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;

namespace RentNearBy.Api.Hubs;

// In-process registry of live ChatHub connections per user (contract 3.1), used to end a revoked session's sockets.
// Single API instance only (like AppTabCache); a scale-out needs a backplane.
public sealed class HubConnectionRegistry
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, HubCallerContext>> _byUser = new();

    public void Add(Guid userId, HubCallerContext context)
        => _byUser.GetOrAdd(userId, _ => new ConcurrentDictionary<string, HubCallerContext>())[context.ConnectionId] = context;

    public void Remove(Guid userId, string connectionId)
    {
        if (!_byUser.TryGetValue(userId, out var connections)) return;
        connections.TryRemove(connectionId, out _);
        if (connections.IsEmpty) _byUser.TryRemove(new KeyValuePair<Guid, ConcurrentDictionary<string, HubCallerContext>>(userId, connections));
    }

    public IReadOnlyList<HubCallerContext> Of(Guid userId)
        => _byUser.TryGetValue(userId, out var connections) ? connections.Values.ToList() : [];
}
