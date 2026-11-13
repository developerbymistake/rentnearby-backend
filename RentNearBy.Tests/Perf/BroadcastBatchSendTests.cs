using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.Perf;

public class BroadcastBatchSendTests
{
    private static readonly Guid U1 = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid U2 = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid U3 = Guid.Parse("00000000-0000-0000-0000-000000000003");

    private static FcmMessageSpec Spec(string token)
        => new(token, "T", "B", new Dictionary<string, string> { ["notification_type"] = "broadcast" });

    [Fact]
    public void Tally_counts_sent_and_failed_and_collects_invalid_tokens()
    {
        var specs = new[] { Spec("a"), Spec("b"), Spec("c"), Spec("d") };
        var outcomes = new[] { FcmSendOutcome.Sent, FcmSendOutcome.InvalidToken, FcmSendOutcome.Failed, FcmSendOutcome.Sent };

        var (sent, failed, invalid) = BroadcastWorkerService.Tally(specs, outcomes);

        Assert.Equal(2, sent);
        Assert.Equal(2, failed);
        Assert.Equal(["b"], invalid);
    }

    [Fact]
    public void Tally_dedupes_invalid_tokens_but_counts_every_row_failed()
    {
        var specs = new[] { Spec("x"), Spec("x"), Spec("y") };
        var outcomes = new[] { FcmSendOutcome.InvalidToken, FcmSendOutcome.InvalidToken, FcmSendOutcome.InvalidToken };

        var (sent, failed, invalid) = BroadcastWorkerService.Tally(specs, outcomes);

        Assert.Equal(0, sent);
        Assert.Equal(3, failed);
        Assert.Equal(["x", "y"], invalid);
    }

    [Fact]
    public void Tally_whole_call_exception_counts_all_failed_with_no_invalidation()
    {
        var specs = new[] { Spec("a"), Spec("b") };

        var (sent, failed, invalid) = BroadcastWorkerService.Tally(specs, null);

        Assert.Equal(0, sent);
        Assert.Equal(2, failed);
        Assert.Empty(invalid);
    }

    [Fact]
    public async Task One_batch_uses_one_token_query_one_send_and_one_invalidation()
    {
        var repo = new FakeTokens([U1, U2, U3], [(U1, "t1"), (U1, "t1b"), (U2, "dead"), (U3, "t3")]);
        var fcm = new FakeFcm(s => s.Token == "dead" ? FcmSendOutcome.InvalidToken : FcmSendOutcome.Sent);

        await BroadcastWorkerService.ProcessBatchAsync(
            new BroadcastMessage { Title = "Hi", Body = "News", Offset = 0, Limit = 100 },
            UnitOfWorkProxy.Create(repo), fcm, NullLogger.Instance);

        Assert.Equal(1, repo.PagedCalls);
        Assert.Equal(1, repo.ByUserIdsCalls);
        Assert.Equal(1, fcm.Calls);
        Assert.Equal(1, repo.MarkManyCalls);
        Assert.Equal(["dead"], repo.Marked);
        Assert.Equal(["t1", "t1b", "dead", "t3"], fcm.Sent.Select(s => s.Token));
        Assert.All(fcm.Sent, s =>
        {
            Assert.Equal("Hi", s.Title);
            Assert.Equal("News", s.Body);
            Assert.Null(s.Tag);
            Assert.Equal(new Dictionary<string, string> { ["notification_type"] = "broadcast" }, s.Data);
        });
    }

    [Fact]
    public async Task No_invalid_tokens_means_no_invalidation_call()
    {
        var repo = new FakeTokens([U1], [(U1, "t1")]);
        var fcm = new FakeFcm(_ => FcmSendOutcome.Failed);

        await BroadcastWorkerService.ProcessBatchAsync(
            new BroadcastMessage { Title = "Hi", Body = "News", Offset = 0, Limit = 100 },
            UnitOfWorkProxy.Create(repo), fcm, NullLogger.Instance);

        Assert.Equal(1, fcm.Calls);
        Assert.Equal(0, repo.MarkManyCalls);
    }

    [Fact]
    public async Task Send_exception_invalidates_nothing()
    {
        var repo = new FakeTokens([U1], [(U1, "t1")]);
        var fcm = new FakeFcm(_ => throw new InvalidOperationException("boom"));

        await BroadcastWorkerService.ProcessBatchAsync(
            new BroadcastMessage { Title = "Hi", Body = "News", Offset = 0, Limit = 100 },
            UnitOfWorkProxy.Create(repo), fcm, NullLogger.Instance);

        Assert.Equal(0, repo.MarkManyCalls);
    }

    private sealed class FakeTokens(IReadOnlyList<Guid> users, IReadOnlyList<(Guid UserId, string Token)> rows) : IDeviceTokenRepository
    {
        public int PagedCalls { get; private set; }
        public int ByUserIdsCalls { get; private set; }
        public int MarkManyCalls { get; private set; }
        public List<string> Marked { get; } = [];

        public Task<IReadOnlyList<Guid>> GetValidTokenUserIdsPagedAsync(int offset, int limit)
        {
            PagedCalls++;
            return Task.FromResult(users);
        }

        public Task<IReadOnlyList<(Guid UserId, string Token)>> GetValidByUserIdsAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
        {
            ByUserIdsCalls++;
            return Task.FromResult<IReadOnlyList<(Guid UserId, string Token)>>(rows.Where(r => userIds.Contains(r.UserId)).ToList());
        }

        public Task<int> MarkInvalidManyAsync(IReadOnlyCollection<string> tokens, CancellationToken ct = default)
        {
            MarkManyCalls++;
            Marked.AddRange(tokens);
            return Task.FromResult(tokens.Count);
        }

        public Task<IEnumerable<DeviceToken>> GetValidByUserIdAsync(Guid userId) => throw new NotSupportedException("per-user lookup is the N+1 path");
        public Task MarkInvalidAsync(string token) => throw new NotSupportedException("per-token update is the N+1 path");
        public Task UpsertAsync(Guid userId, string token) => throw new NotSupportedException();
        public Task DeleteByUserIdAsync(Guid userId) => throw new NotSupportedException();
        public Task DeleteForUserAsync(Guid userId, string token) => throw new NotSupportedException();
        public Task<int> GetValidTokenUserCountAsync() => throw new NotSupportedException();
        public Task<IReadOnlyList<(Guid Id, string Token)>> GetValidTokensByRoleAfterAsync(Guid roleId, Guid? afterId, int limit) => throw new NotSupportedException();
    }

    private sealed class FakeFcm(Func<FcmMessageSpec, FcmSendOutcome> decide) : IFcmService
    {
        public List<FcmMessageSpec> Sent { get; } = [];
        public int Calls { get; private set; }

        public Task<IReadOnlyList<FcmSendOutcome>> SendEachAsync(IReadOnlyList<FcmMessageSpec> messages, CancellationToken ct = default)
        {
            Calls++;
            Sent.AddRange(messages);
            return Task.FromResult<IReadOnlyList<FcmSendOutcome>>(messages.Select(decide).ToList());
        }

        public Task<bool> SendAsync(string token, string title, string body, string notificationType, IDictionary<string, string>? data = null) => throw new NotSupportedException();
        public Task<bool> SendAsync(string token, string title, string body, string notificationType, NotificationDestination destination) => throw new NotSupportedException();
        public Task<bool> SendToTopicAsync(string topic, string title, string body, IDictionary<string, string>? data = null) => throw new NotSupportedException();
    }

    public class UnitOfWorkProxy : DispatchProxy
    {
        private IDeviceTokenRepository _tokens = null!;

        public static IUnitOfWork Create(IDeviceTokenRepository tokens)
        {
            var uow = DispatchProxy.Create<IUnitOfWork, UnitOfWorkProxy>();
            ((UnitOfWorkProxy)(object)uow)._tokens = tokens;
            return uow;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name == "get_DeviceTokens"
                ? _tokens
                : throw new NotSupportedException($"Unexpected unit-of-work call {targetMethod?.Name}");
    }
}
