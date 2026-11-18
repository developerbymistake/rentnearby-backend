using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace RentNearBy.Infrastructure.Services.TaxiRequests;

// Log event ids of the taxi request system (MASTER_PLAN 2.9). Ids only in log text, never names or phones.
public static class TaxiRequestLogEvents
{
    public static readonly EventId PushDead = new(7003, "push.dead");
    public static readonly EventId TripTimerPassFailed = new(7004, "trip.timer.pass_failed");
    public static readonly EventId RouteCityUnresolved = new(7101, "taxi.route.city_unresolved");
    public static readonly EventId DueParked = new(7110, "taxi.due.parked");
    public static readonly EventId DueLag = new(7111, "taxi.due.lag");
    public static readonly EventId MatchPassFailed = new(7120, "taxi.match.pass_failed");
    public static readonly EventId RequestStuck = new(7121, "taxi.request.stuck");
    public static readonly EventId PushAckLow = new(7130, "taxi.push.ack_low");
}

// Singleton. Tags carry outcomes and states only, never user, driver or request ids.
public sealed class TaxiRequestMetrics : IDisposable
{
    public const string MeterName = "RentNearBy.TaxiRequests";

    private readonly Meter _meter;
    private readonly bool _ownsMeter;
    private readonly Histogram<double> _dispatchDuration;
    private readonly Histogram<double> _dueLag;
    private readonly Counter<long> _offers;
    private readonly Counter<long> _requests;
    private readonly Counter<long> _ringSent;
    private readonly Counter<long> _ringAcked;
    private readonly Histogram<double> _ackDelay;
    private readonly Counter<long> _overlapRejected;

    public TaxiRequestMetrics(IMeterFactory? meterFactory = null)
    {
        _ownsMeter = meterFactory is null;
        _meter = meterFactory?.Create(MeterName) ?? new Meter(MeterName);
        _dispatchDuration = _meter.CreateHistogram<double>("taxi.dispatch.duration", "s");
        _dueLag = _meter.CreateHistogram<double>("taxi.due_actions.lag", "s");
        _offers = _meter.CreateCounter<long>("taxi.offers");
        _requests = _meter.CreateCounter<long>("taxi.requests");
        _ringSent = _meter.CreateCounter<long>("taxi.push.ring_sent");
        _ringAcked = _meter.CreateCounter<long>("taxi.push.ring_acked");
        _ackDelay = _meter.CreateHistogram<double>("taxi.push.ack_delay", "s");
        _overlapRejected = _meter.CreateCounter<long>("taxi.booking.overlap_rejected");
    }

    // Listeners compare against this instance, so measurements from other TaxiRequestMetrics instances are not mixed in.
    public Meter Meter => _meter;

    public void RecordDispatch(double seconds) => _dispatchDuration.Record(Math.Max(0, seconds));

    public void RecordDueLag(double seconds) => _dueLag.Record(Math.Max(0, seconds));

    public void OfferEnded(string outcome) => _offers.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    public void RequestEnded(string finalState) => _requests.Add(1, new KeyValuePair<string, object?>("state", finalState));

    public void RingSent() => _ringSent.Add(1);

    public void RingAcked(double delaySeconds)
    {
        _ringAcked.Add(1);
        _ackDelay.Record(Math.Max(0, delaySeconds));
    }

    public void OverlapRejected() => _overlapRejected.Add(1);

    public void Dispose()
    {
        if (_ownsMeter) _meter.Dispose();
    }
}
