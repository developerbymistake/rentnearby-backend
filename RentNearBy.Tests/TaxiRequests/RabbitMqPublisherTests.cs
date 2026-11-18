using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.TaxiRequests;

public class RabbitMqPublisherTests
{
    private sealed class FakeChannel : IRabbitPublishChannel
    {
        public bool IsOpen { get; set; } = true;
        public bool Disposed { get; private set; }
        public List<(string Queue, IDictionary<string, object?>? Arguments)> Declares { get; } = [];
        public List<string> Publishes { get; } = [];
        public List<string> MessageIds { get; } = [];
        public Func<string, Exception?> PublishFault { get; set; } = _ => null;
        public Func<CancellationToken, Task>? Confirm { get; set; }

        public Task DeclareQueueAsync(string queue, IDictionary<string, object?>? arguments, CancellationToken ct)
        {
            Declares.Add((queue, arguments));
            return Task.CompletedTask;
        }

        public Task PublishConfirmedAsync(string queue, ReadOnlyMemory<byte> body, string messageId, CancellationToken ct)
        {
            lock (Publishes)
            {
                Publishes.Add(queue);
                MessageIds.Add(messageId);
            }
            var fault = PublishFault(queue);
            if (fault != null) return Task.FromException(fault);
            return Confirm?.Invoke(ct) ?? Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            IsOpen = false;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeFactory : IRabbitChannelFactory
    {
        public List<FakeChannel> Channels { get; } = [];
        public Func<CancellationToken, Task>? BeforeCreate { get; set; }
        public Action<FakeChannel> Configure { get; set; } = _ => { };

        public async Task<IRabbitPublishChannel> CreateChannelAsync(CancellationToken ct)
        {
            if (BeforeCreate != null) await BeforeCreate(ct);
            var channel = new FakeChannel();
            Configure(channel);
            Channels.Add(channel);
            return channel;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static (RabbitMqPublisher Publisher, FakeFactory Factory, List<TimeSpan> Delays) Build(bool realDelay = false)
    {
        var factory = new FakeFactory();
        var delays = new List<TimeSpan>();
        var publisher = new RabbitMqPublisher(factory, NullLogger<RabbitMqPublisher>.Instance, d =>
        {
            delays.Add(d);
            return realDelay ? Task.Delay(d) : Task.CompletedTask;
        });
        return (publisher, factory, delays);
    }

    private static Task Hang(CancellationToken ct) => Task.Delay(Timeout.Infinite, ct);

    [Fact]
    public async Task One_channel_serves_many_publishes()
    {
        var (publisher, factory, _) = Build();

        await Task.WhenAll(Enumerable.Range(0, 50).Select(i => publisher.PublishAsync(i % 2 == 0 ? "a" : "b", $"m{i}")));

        var channel = Assert.Single(factory.Channels);
        Assert.Equal(50, channel.Publishes.Count);
        Assert.False(channel.Disposed);
    }

    [Fact]
    public async Task Queue_is_declared_once()
    {
        var (publisher, factory, _) = Build();

        for (var i = 0; i < 5; i++)
        {
            await publisher.PublishAsync("notification.push", "x");
            await publisher.PublishAsync("report.filed", "y");
        }

        var channel = Assert.Single(factory.Channels);
        Assert.Equal(["notification.push", "report.filed"], channel.Declares.Select(d => d.Queue).ToArray());
        Assert.Equal(10, channel.Publishes.Count);
    }

    [Fact]
    public async Task Closed_channel_is_recreated()
    {
        var (publisher, factory, _) = Build();

        await publisher.PublishAsync("q", "1");
        factory.Channels[0].IsOpen = false;
        await publisher.PublishAsync("q", "2");

        Assert.Equal(2, factory.Channels.Count);
        Assert.True(factory.Channels[0].Disposed);
        Assert.Single(factory.Channels[0].Declares);
        Assert.Single(factory.Channels[1].Declares);
        Assert.Single(factory.Channels[1].Publishes);
    }

    [Fact]
    public async Task Broken_channel_is_replaced_on_retry()
    {
        var (publisher, factory, delays) = Build();
        var first = true;
        factory.Configure = c =>
        {
            if (!first) return;
            first = false;
            c.PublishFault = _ => new IOException("connection reset");
        };

        await publisher.PublishAsync("q", "1");

        Assert.Equal(2, factory.Channels.Count);
        Assert.True(factory.Channels[0].Disposed);
        Assert.Single(factory.Channels[1].Declares);
        Assert.Single(factory.Channels[1].Publishes);
        Assert.Equal([TimeSpan.FromMilliseconds(200)], delays.ToArray());
    }

    [Fact]
    public async Task Unconfirmed_publish_retries_then_throws()
    {
        var (publisher, factory, delays) = Build();
        factory.Configure = c => c.PublishFault = q => new RabbitPublishNotConfirmedException(q);

        await Assert.ThrowsAsync<RabbitPublishNotConfirmedException>(() => publisher.PublishAsync("q", "x"));

        var channel = Assert.Single(factory.Channels);
        Assert.Equal(4, channel.Publishes.Count);
        Assert.Single(channel.Declares);
        Assert.Equal(
            [TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)],
            delays.ToArray());
        Assert.True(delays.Aggregate(TimeSpan.Zero, (a, d) => a + d) < TimeSpan.FromSeconds(4));
    }

    [Fact]
    public async Task Queue_arguments_stay_null()
    {
        var (publisher, factory, _) = Build();

        await publisher.PublishAsync("stayjob.posted.seekers", "x");
        factory.Channels[0].IsOpen = false;
        await publisher.PublishAsync("stayjob.posted.seekers", "y");

        var declares = factory.Channels.SelectMany(c => c.Declares).ToList();
        Assert.Equal(2, declares.Count);
        Assert.All(declares, d => Assert.Null(d.Arguments));
    }

    [Fact]
    public void Retry_waits_fit_inside_the_total_budget()
    {
        var waits = RabbitMqPublisher.RetryDelays.Aggregate(TimeSpan.Zero, (a, d) => a + d);
        Assert.True(waits < RabbitMqPublisher.TotalBudget);
        Assert.True(RabbitMqPublisher.TotalBudget < TimeSpan.FromSeconds(4));
        Assert.True(RabbitMqPublisher.AttemptTimeout < RabbitMqPublisher.TotalBudget);
    }

    [Fact]
    public async Task Hanging_confirm_gives_up_within_four_seconds()
    {
        var (publisher, factory, _) = Build(realDelay: true);
        factory.Configure = c => c.Confirm = Hang;

        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() => publisher.PublishAsync("q", "x"));
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4), $"took {watch.Elapsed}");
        Assert.True(watch.Elapsed >= RabbitMqPublisher.AttemptTimeout);
        Assert.True(factory.Channels.Count >= 2);
        Assert.All(factory.Channels, c => Assert.True(c.Disposed));
    }

    [Fact]
    public async Task Hanging_connect_gives_up_within_four_seconds()
    {
        var (publisher, factory, _) = Build(realDelay: true);
        factory.BeforeCreate = Hang;

        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() => publisher.PublishAsync("q", "x"));
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4), $"took {watch.Elapsed}");
        Assert.Empty(factory.Channels);
    }

    [Fact]
    public async Task Timed_out_try_drops_channel_and_retries_on_a_fresh_one_with_the_same_message_id()
    {
        var (publisher, factory, delays) = Build();
        var first = true;
        factory.Configure = c =>
        {
            if (!first) return;
            first = false;
            c.Confirm = Hang;
        };

        await publisher.PublishAsync("q", "1");

        Assert.Equal(2, factory.Channels.Count);
        Assert.True(factory.Channels[0].Disposed);
        Assert.False(factory.Channels[1].Disposed);
        Assert.Equal([TimeSpan.FromMilliseconds(200)], delays.ToArray());
        var id = Assert.Single(factory.Channels[0].MessageIds);
        Assert.Equal([id], factory.Channels[1].MessageIds.ToArray());
    }

    [Fact]
    public async Task Different_publishes_get_different_message_ids()
    {
        var (publisher, factory, _) = Build();

        await publisher.PublishAsync("q", "1");
        await publisher.PublishAsync("q", "2");

        var ids = Assert.Single(factory.Channels).MessageIds;
        Assert.Equal(2, ids.Distinct().Count());
    }

    [Fact]
    public async Task Slow_confirms_overlap_on_one_channel()
    {
        var (publisher, factory, delays) = Build();
        var inFlight = 0;
        var maxInFlight = 0;
        factory.Configure = c => c.Confirm = async ct =>
        {
            var now = Interlocked.Increment(ref inFlight);
            InterlockedMax(ref maxInFlight, now);
            await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
            Interlocked.Decrement(ref inFlight);
        };

        var watch = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, 60).Select(i => publisher.PublishAsync("q", $"m{i}")));
        watch.Stop();

        var channel = Assert.Single(factory.Channels);
        Assert.Equal(60, channel.Publishes.Count);
        Assert.Empty(delays);
        Assert.True(maxInFlight > 1);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"took {watch.Elapsed}");
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref target)))
            if (Interlocked.CompareExchange(ref target, value, seen) == seen) return;
    }

    [Fact]
    public void Public_constructor_and_interface_are_unchanged()
    {
        var ctor = typeof(RabbitMqPublisher).GetConstructors();
        var only = Assert.Single(ctor);
        Assert.Equal(
            [typeof(Microsoft.Extensions.Configuration.IConfiguration), typeof(Microsoft.Extensions.Logging.ILogger<RabbitMqPublisher>)],
            only.GetParameters().Select(p => p.ParameterType).ToArray());
        Assert.True(typeof(RentNearBy.Core.Interfaces.IRabbitMqPublisher).IsAssignableFrom(typeof(RabbitMqPublisher)));
    }
}
