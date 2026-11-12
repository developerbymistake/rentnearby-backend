using System.Net.Sockets;
using RabbitMQ.Client.Exceptions;
using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.Journeys;

// SB5: RabbitMQ is down for a long time while travellers send bulk enquiries. After it returns, every host still gets the push.
public class B3OutboxOutageTests
{
    private static readonly DateTime T0 = new(2026, 11, 12, 6, 0, 0, DateTimeKind.Utc);

    private sealed class DownPublisher : IConfirmedPublisher
    {
        public Task<IReadOnlyList<PublishResult>> PublishBatchAsync(IReadOnlyList<ConfirmedMessage> messages, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PublishResult>>(
                messages.Select(_ => new PublishResult(false, OutboxRelayLogic.UnavailablePrefix + "connection refused")).ToList());
    }

    private sealed class NackPublisher : IConfirmedPublisher
    {
        public Task<IReadOnlyList<PublishResult>> PublishBatchAsync(IReadOnlyList<ConfirmedMessage> messages, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PublishResult>>(messages.Select(_ => new PublishResult(false, "nack")).ToList());
    }

    private static OutboxRow Row(short attempts = 0) => new()
    {
        Id = 1, Key = "k1", Exchange = "rnb.push", RoutingKey = "send", Payload = "{}", Attempts = attempts,
    };

    [Fact]
    public async Task Twenty_straight_broker_outages_keep_the_row_at_zero_attempts_and_due_again()
    {
        var row = Row();
        var now = T0;
        for (var i = 0; i < 20; i++)
        {
            var outcome = await OutboxRelayLogic.PublishAsync([row], new DownPublisher(), now, CancellationToken.None);
            var failure = Assert.Single(outcome.Failed);
            Assert.Equal(0, failure.Attempts);
            Assert.Equal(now.AddSeconds(OutboxRelayLogic.UnavailableRetrySeconds), failure.NextAttemptAt);
            row.Attempts = failure.Attempts;
            now = failure.NextAttemptAt;
        }
        Assert.Equal(0, row.Attempts);
        Assert.False(OutboxRelayLogic.IsGivenUp(T0, now));
    }

    [Fact]
    public async Task A_broker_outage_of_twenty_minutes_does_not_lose_the_row_and_it_goes_out_after()
    {
        var row = Row();
        var now = T0;
        while (now < T0.AddMinutes(20))
        {
            var outcome = await OutboxRelayLogic.PublishAsync([row], new DownPublisher(), now, CancellationToken.None);
            row.Attempts = outcome.Failed[0].Attempts;
            now = outcome.Failed[0].NextAttemptAt;
        }
        var back = await OutboxRelayLogic.PublishAsync([row], new FakeOk(), now, CancellationToken.None);
        Assert.Equal([1L], back.Published);
    }

    private sealed class FakeOk : IConfirmedPublisher
    {
        public Task<IReadOnlyList<PublishResult>> PublishBatchAsync(IReadOnlyList<ConfirmedMessage> messages, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PublishResult>>(messages.Select(_ => new PublishResult(true)).ToList());
    }

    [Fact]
    public async Task A_per_message_nack_still_counts_an_attempt_and_backs_off()
    {
        var outcome = await OutboxRelayLogic.PublishAsync([Row(attempts: 2)], new NackPublisher(), T0, CancellationToken.None);
        var failure = Assert.Single(outcome.Failed);
        Assert.Equal(3, failure.Attempts);
        Assert.Equal(T0.AddSeconds(8), failure.NextAttemptAt);
    }

    [Theory]
    [InlineData(71, false)]
    [InlineData(72, false)]
    [InlineData(73, true)]
    public void A_row_is_given_up_by_age_not_by_attempts(int hours, bool givenUp)
        => Assert.Equal(givenUp, OutboxRelayLogic.IsGivenUp(T0, T0.AddHours(hours).AddMinutes(hours == 72 ? 0 : 1)));

    [Fact]
    public void Channel_and_connection_errors_are_unavailable_but_a_nack_is_not()
    {
        Assert.True(ConfirmedRabbitPublisher.IsBrokerUnavailable(new SocketException()));
        Assert.True(ConfirmedRabbitPublisher.IsBrokerUnavailable(new IOException("closed")));
        Assert.True(ConfirmedRabbitPublisher.IsBrokerUnavailable(new BrokerUnreachableException(new IOException("x"))));
        Assert.True(ConfirmedRabbitPublisher.IsBrokerUnavailable(new ObjectDisposedException("channel")));
        Assert.False(ConfirmedRabbitPublisher.IsBrokerUnavailable(new InvalidOperationException("nack")));
    }

    [Fact]
    public void Backlog_levels_follow_the_age_of_the_oldest_unpublished_row()
    {
        Assert.Equal(OutboxBacklogLevel.Ok, OutboxRelayLogic.BacklogLevel(0, null, T0));
        Assert.Equal(OutboxBacklogLevel.Ok, OutboxRelayLogic.BacklogLevel(5, T0.AddMinutes(-2), T0));
        Assert.Equal(OutboxBacklogLevel.Warning, OutboxRelayLogic.BacklogLevel(5, T0.AddMinutes(-10), T0));
        Assert.Equal(OutboxBacklogLevel.Critical, OutboxRelayLogic.BacklogLevel(5, T0.AddMinutes(-45), T0));
        Assert.Equal(OutboxBacklogLevel.Critical, OutboxRelayLogic.BacklogLevel(5, T0.AddHours(-73), T0));
    }
}
