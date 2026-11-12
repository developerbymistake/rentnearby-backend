using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.Trips;

public class OutboxRelayTests
{
    private static readonly DateTime Now = new(2026, 11, 12, 0, 0, 0, DateTimeKind.Utc);

    private sealed class FakePublisher(Func<ConfirmedMessage, PublishResult> decide) : IConfirmedPublisher
    {
        public List<ConfirmedMessage> Sent { get; } = [];

        public Task<IReadOnlyList<PublishResult>> PublishBatchAsync(IReadOnlyList<ConfirmedMessage> messages, CancellationToken ct = default)
        {
            Sent.AddRange(messages);
            return Task.FromResult<IReadOnlyList<PublishResult>>(messages.Select(decide).ToList());
        }
    }

    private static OutboxRow Row(long id, short attempts = 0) => new()
    {
        Id = id, Key = $"k{id}", Exchange = "rnb.push", RoutingKey = "send", Payload = $"{{\"n\":{id}}}", Attempts = attempts,
    };

    [Fact]
    public async Task One_message_per_row_with_the_row_key_as_message_id()
    {
        var pub = new FakePublisher(_ => new PublishResult(true));
        var outcome = await OutboxRelayLogic.PublishAsync([Row(1), Row(2), Row(3)], pub, Now, CancellationToken.None);

        Assert.Equal([1L, 2L, 3L], outcome.Published);
        Assert.Empty(outcome.Failed);
        Assert.Equal(["k1", "k2", "k3"], pub.Sent.Select(m => m.MessageId));
        Assert.All(pub.Sent, m => Assert.Equal(("rnb.push", "send"), (m.Exchange, m.RoutingKey)));
    }

    [Fact]
    public async Task Only_confirmed_rows_are_marked_published_and_the_rest_back_off()
    {
        var pub = new FakePublisher(m => m.MessageId == "k2" ? new PublishResult(false, "nack") : new PublishResult(true));
        var outcome = await OutboxRelayLogic.PublishAsync([Row(1), Row(2, attempts: 2), Row(3)], pub, Now, CancellationToken.None);

        Assert.Equal([1L, 3L], outcome.Published);
        var failure = Assert.Single(outcome.Failed);
        Assert.Equal(2, failure.Id);
        Assert.Equal(3, failure.Attempts);
        Assert.Equal(Now.AddSeconds(8), failure.NextAttemptAt);
        Assert.Equal("nack", failure.Error);
    }

    [Fact]
    public async Task A_broker_outage_fails_every_row_without_throwing()
    {
        var pub = new FakePublisher(_ => new PublishResult(false, "broker down"));
        var outcome = await OutboxRelayLogic.PublishAsync([Row(1), Row(2)], pub, Now, CancellationToken.None);

        Assert.Empty(outcome.Published);
        Assert.Equal(2, outcome.Failed.Count);
        Assert.All(outcome.Failed, f => Assert.Equal((short)1, f.Attempts));
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    [InlineData(8, 256)]
    [InlineData(9, 300)]
    [InlineData(30, 300)]
    public void Backoff_doubles_and_caps_at_five_minutes(int attempts, int seconds)
        => Assert.Equal(TimeSpan.FromSeconds(seconds), OutboxRelayLogic.Backoff(attempts));

    [Fact]
    public void Batch_size_and_give_up_age_follow_the_plan()
    {
        Assert.Equal(500, OutboxRelayLogic.BatchSize);
        Assert.Equal(TimeSpan.FromHours(72), OutboxRelayLogic.GiveUpAfter);
    }

    [Fact]
    public async Task A_short_result_list_counts_the_missing_rows_as_failed()
    {
        var short1 = new ShortPublisher();
        var outcome = await OutboxRelayLogic.PublishAsync([Row(1), Row(2)], short1, Now, CancellationToken.None);
        Assert.Equal([1L], outcome.Published);
        Assert.Equal(2, Assert.Single(outcome.Failed).Id);
    }

    private sealed class ShortPublisher : IConfirmedPublisher
    {
        public Task<IReadOnlyList<PublishResult>> PublishBatchAsync(IReadOnlyList<ConfirmedMessage> messages, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PublishResult>>([new PublishResult(true)]);
    }

    [Fact]
    public void Signal_wakes_the_waiter_and_never_throws_when_woken_twice()
    {
        var signal = new OutboxSignal();
        signal.Wake();
        signal.Wake();
        Assert.True(signal.WaitAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None).Result);
        Assert.False(signal.WaitAsync(TimeSpan.FromMilliseconds(20), CancellationToken.None).Result);
    }
}
