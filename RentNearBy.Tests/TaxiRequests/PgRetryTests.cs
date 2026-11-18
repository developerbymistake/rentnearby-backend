using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RentNearBy.Infrastructure.Services.TaxiRequests;

namespace RentNearBy.Tests.TaxiRequests;

public class PgRetryTests
{
    private static PostgresException Pg(string sqlState) => new("conflict", "ERROR", "ERROR", sqlState);

    [Theory]
    [InlineData("40P01")]
    [InlineData("40001")]
    public async Task Deadlock_or_serialization_failure_retries_the_unit_once(string sqlState)
    {
        var calls = 0;
        var cleared = 0;
        var result = await PgRetry.OnceAsync(_ =>
        {
            calls++;
            if (calls == 1) throw new DbUpdateException("wrapped", Pg(sqlState));
            return Task.FromResult(42);
        }, CancellationToken.None, beforeRetry: () => cleared++);

        Assert.Equal(42, result);
        Assert.Equal(2, calls);
        Assert.Equal(1, cleared);
    }

    [Fact]
    public async Task Second_failure_is_thrown_not_retried_again()
    {
        var calls = 0;
        await Assert.ThrowsAsync<PostgresException>(() => PgRetry.OnceAsync(_ =>
        {
            calls++;
            throw Pg("40P01");
        }, CancellationToken.None));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Other_errors_are_not_retried()
    {
        var calls = 0;
        await Assert.ThrowsAsync<PostgresException>(() => PgRetry.OnceAsync(_ =>
        {
            calls++;
            throw Pg("23505");
        }, CancellationToken.None));
        Assert.Equal(1, calls);
        Assert.False(PgRetry.IsRetryable(new InvalidOperationException("x")));
        Assert.False(PgRetry.IsRetryable(null));
    }

    [Fact]
    public void Metrics_publish_under_the_taxi_requests_meter_without_id_tags()
    {
        var events = new ConcurrentQueue<(string Name, double Value, string[] TagKeys)>();
        using var metrics = new TaxiRequestMetrics();
        Assert.Equal(TaxiRequestMetrics.MeterName, metrics.Meter.Name);
        using var listener = new MeterListener();
        // Other tests record on their own TaxiRequestMetrics in parallel; only this instance's meter is listened to.
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter, metrics.Meter)) l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((i, v, tags, _) => events.Enqueue((i.Name, v, tags.ToArray().Select(t => t.Key).ToArray())));
        listener.SetMeasurementEventCallback<long>((i, v, tags, _) => events.Enqueue((i.Name, v, tags.ToArray().Select(t => t.Key).ToArray())));
        listener.Start();

        metrics.RecordDueLag(-3);
        metrics.RecordDispatch(0.2);
        metrics.OfferEnded("Missed");
        metrics.RequestEnded("Expired");
        metrics.RingSent();
        metrics.RingAcked(1.5);
        metrics.OverlapRejected();

        var seen = events.ToArray();
        Assert.Equal(8, seen.Length);
        Assert.Contains(seen, s => s.Name == "taxi.due_actions.lag" && s.Value == 0);
        Assert.Contains(seen, s => s.Name == "taxi.dispatch.duration");
        Assert.Contains(seen, s => s.Name == "taxi.offers" && s.TagKeys.SequenceEqual(["outcome"]));
        Assert.Contains(seen, s => s.Name == "taxi.requests" && s.TagKeys.SequenceEqual(["state"]));
        Assert.Contains(seen, s => s.Name == "taxi.push.ring_sent");
        Assert.Contains(seen, s => s.Name == "taxi.push.ring_acked");
        Assert.Contains(seen, s => s.Name == "taxi.push.ack_delay" && s.Value == 1.5);
        Assert.Contains(seen, s => s.Name == "taxi.booking.overlap_rejected");
        Assert.DoesNotContain(seen, s => s.TagKeys.Any(k => k.Contains("id", StringComparison.OrdinalIgnoreCase)));
    }
}
