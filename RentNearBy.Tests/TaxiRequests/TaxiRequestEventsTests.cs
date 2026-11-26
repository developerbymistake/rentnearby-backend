using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using RentNearBy.Api.Hubs;
using RentNearBy.Api.Services;
using RentNearBy.Core.DTOs.TaxiRequests;
using RentNearBy.Core.Interfaces;

namespace RentNearBy.Tests.TaxiRequests;

public class TaxiRequestEventsTests
{
    private static readonly Guid RequestId = Guid.Parse("3f9a6c2d-1b7e-4f0a-8c55-2e7d9b4a6101");
    private static readonly Guid TripId = Guid.Parse("7b1c2f0e-5a44-4c1e-9d3a-0f6f2a1b9c01");

    private sealed class Proxy(bool fail) : IClientProxy
    {
        public readonly List<(string Method, object?[] Args)> Sent = [];
        public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default)
        {
            if (fail) throw new InvalidOperationException("hub down");
            Sent.Add((method, args));
            return Task.CompletedTask;
        }
    }

    private sealed class Clients(Proxy proxy) : IHubClients
    {
        public readonly List<string> Groups = [];
        public readonly List<string> Other = [];
        public IClientProxy Group(string groupName) { Groups.Add(groupName); return proxy; }
        public IClientProxy All { get { Other.Add("All"); return proxy; } }
        public IClientProxy AllExcept(IReadOnlyList<string> c) { Other.Add("AllExcept"); return proxy; }
        public IClientProxy Client(string c) { Other.Add("Client"); return proxy; }
        public IClientProxy Clients_(IReadOnlyList<string> c) { Other.Add("Clients"); return proxy; }
        IClientProxy IHubClients<IClientProxy>.Clients(IReadOnlyList<string> c) => Clients_(c);
        public IClientProxy GroupExcept(string g, IReadOnlyList<string> c) { Other.Add("GroupExcept"); return proxy; }
        public IClientProxy Groups_(IReadOnlyList<string> g) { Other.Add("Groups"); return proxy; }
        IClientProxy IHubClients<IClientProxy>.Groups(IReadOnlyList<string> g) => Groups_(g);
        public IClientProxy User(string u) { Other.Add("User"); return proxy; }
        public IClientProxy Users(IReadOnlyList<string> u) { Other.Add("Users"); return proxy; }
    }

    private sealed class HubCtx(IHubClients clients) : IHubContext<NotificationHub>
    {
        public IHubClients Clients { get; } = clients;
        public IGroupManager Groups { get; } = new GroupManager();
    }

    internal sealed class GroupManager : IGroupManager
    {
        public readonly List<(string Conn, string Group)> Added = [];
        public readonly List<(string Conn, string Group)> Removed = [];
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken ct = default) { Added.Add((connectionId, groupName)); return Task.CompletedTask; }
        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken ct = default) { Removed.Add((connectionId, groupName)); return Task.CompletedTask; }
    }

    internal sealed class CallerContext(string connectionId, ClaimsPrincipal user) : HubCallerContext
    {
        public override string ConnectionId { get; } = connectionId;
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User { get; } = user;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }
    }

    // The live role claim is set at token validation (RoleClaimSync); the subscription is for a SEEKER caller only.
    internal static ClaimsPrincipal Caller(Guid? userId, string? role = UserRoles.Seeker)
    {
        var claims = new List<Claim>();
        if (userId is { } id) claims.Add(new Claim(ClaimTypes.NameIdentifier, id.ToString()));
        if (role != null) claims.Add(new Claim(ClaimTypes.Role, role));
        return new(new ClaimsIdentity(claims, "test"));
    }

    private static (NotificationHub Hub, GroupManager Groups) Hub(ClaimsPrincipal user)
    {
        var groups = new GroupManager();
        return (new NotificationHub { Context = new CallerContext("c1", user), Groups = groups }, groups);
    }

    private sealed class CapturingLog : ILogger<TaxiRequestEvents>
    {
        public readonly List<(LogLevel Level, Exception? Ex)> Entries = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, exception));
    }

    [Fact]
    public async Task Event_payload_equals_the_contract_sample()
    {
        var proxy = new Proxy(false);
        var events = new TaxiRequestEvents(new HubCtx(new Clients(proxy)), new CapturingLog());
        await events.RequestChangedAsync(RequestId, TripId, Guid.NewGuid(), 7, "Collecting");

        var (method, args) = Assert.Single(proxy.Sent);
        Assert.Equal("TaxiRequestChanged", method);
        var arg = Assert.Single(args);
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        using var sent = JsonDocument.Parse(JsonSerializer.Serialize(arg, web));
        using var sample = JsonDocument.Parse(File.ReadAllText(Path.Combine(ContractSamplesGuardTests.SamplesDir, "signalr_taxi_request_changed.json")));
        AssertSameJson(sample.RootElement, sent.RootElement, "$");
        Assert.IsType<TaxiRequestChangedEvent>(arg);
    }

    [Fact]
    public async Task Event_goes_to_the_request_group_only()
    {
        var proxy = new Proxy(false);
        var clients = new Clients(proxy);
        await new TaxiRequestEvents(new HubCtx(clients), new CapturingLog()).RequestChangedAsync(RequestId, TripId, Guid.NewGuid(), 1, "Open");

        Assert.Equal([$"taxi_request_{RequestId:D}"], clients.Groups);
        Assert.Empty(clients.Other);
    }

    [Fact]
    public async Task Hub_failure_is_swallowed_and_logged()
    {
        var log = new CapturingLog();
        var events = new TaxiRequestEvents(new HubCtx(new Clients(new Proxy(true))), log);

        await events.RequestChangedAsync(RequestId, TripId, Guid.NewGuid(), 1, "Open");

        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.IsType<InvalidOperationException>(entry.Ex);
    }

    [Fact]
    public async Task Subscribe_by_a_stranger_joins_nothing()
    {
        var owner = Guid.NewGuid();
        var groups = new GroupManager();

        Assert.False(await TaxiRequestSubscriptions.JoinIfTravellerAsync(groups, "c1", Guid.NewGuid(), RequestId, () => Task.FromResult<Guid?>(owner)));
        Assert.False(await TaxiRequestSubscriptions.JoinIfTravellerAsync(groups, "c1", owner, RequestId, () => Task.FromResult<Guid?>(null)));
        Assert.Empty(groups.Added);

        groups.Added.Clear();
        Assert.True(await TaxiRequestSubscriptions.JoinIfTravellerAsync(groups, "c2", owner, RequestId, () => Task.FromResult<Guid?>(owner)));
        Assert.Equal([("c2", $"taxi_request_{RequestId:D}")], groups.Added);
    }

    [Fact]
    public async Task Hub_subscribe_with_a_bad_id_or_no_user_claim_joins_nothing_and_never_reads()
    {
        // A null context proves no database read happens.
        var (hub, groups) = Hub(Caller(Guid.NewGuid()));
        await hub.SubscribeTaxiRequest("not-a-guid", null!);
        await hub.SubscribeTaxiRequest("", null!);

        var (anon, anonGroups) = Hub(Caller(null));
        await anon.SubscribeTaxiRequest(RequestId.ToString(), null!);

        Assert.Empty(groups.Added);
        Assert.Empty(anonGroups.Added);
    }

    // Seeker-only contract 4.4: an owner-role caller joins nothing and never reads, even with a valid id (a null context proves it).
    [Theory]
    [InlineData(UserRoles.TaxiDriver)]
    [InlineData(UserRoles.StayOwner)]
    [InlineData(null)]
    public async Task Hub_subscribe_by_a_non_seeker_joins_nothing_and_never_reads(string? role)
    {
        var (hub, groups) = Hub(Caller(Guid.NewGuid(), role));
        await hub.SubscribeTaxiRequest(RequestId.ToString(), null!);
        Assert.Empty(groups.Added);
    }

    // Contract section 9 names ownership as the rule beside the seeker role; reads stay open while the master switch is off (section 1).
    [Fact]
    public void Hub_subscribe_does_not_depend_on_the_master_switch()
    {
        var method = typeof(NotificationHub).GetMethod(nameof(NotificationHub.SubscribeTaxiRequest))!;
        Assert.DoesNotContain(method.GetParameters(), p => p.ParameterType == typeof(ITripExecutionFeatureReader));
    }

    [Fact]
    public async Task Hub_unsubscribe_leaves_the_request_group_and_ignores_a_bad_id()
    {
        var (hub, groups) = Hub(Caller(Guid.NewGuid()));

        await hub.UnsubscribeTaxiRequest(RequestId.ToString());
        await hub.UnsubscribeTaxiRequest("nope");

        Assert.Equal([("c1", $"taxi_request_{RequestId:D}")], groups.Removed);
        Assert.Empty(groups.Added);
    }

    [Fact]
    public void Json_compare_sees_a_space_inside_a_string_value()
    {
        using var a = JsonDocument.Parse("""{"state":"Open now"}""");
        using var b = JsonDocument.Parse("""{"state":"Opennow"}""");
        Assert.ThrowsAny<Exception>(() => AssertSameJson(a.RootElement, b.RootElement, "$"));
    }

    internal static void AssertSameJson(JsonElement expected, JsonElement actual, string path)
    {
        Assert.True(expected.ValueKind == actual.ValueKind, $"{path}: kind {expected.ValueKind} vs {actual.ValueKind}");
        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                var want = expected.EnumerateObject().Select(p => p.Name).ToList();
                var got = actual.EnumerateObject().Select(p => p.Name).ToList();
                Assert.Equal(want, got);
                foreach (var name in want) AssertSameJson(expected.GetProperty(name), actual.GetProperty(name), $"{path}.{name}");
                break;
            case JsonValueKind.Array:
                Assert.Equal(expected.GetArrayLength(), actual.GetArrayLength());
                for (var i = 0; i < expected.GetArrayLength(); i++) AssertSameJson(expected[i], actual[i], $"{path}[{i}]");
                break;
            case JsonValueKind.String:
                Assert.Equal(expected.GetString(), actual.GetString());
                break;
            case JsonValueKind.Number:
                Assert.Equal(expected.GetRawText(), actual.GetRawText());
                break;
        }
    }
}
