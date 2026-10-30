namespace RentNearBy.Tests.Support;

// Production reads DateTime.UtcNow directly; this clock feeds code that takes "now" as a parameter.
public sealed class FakeClock(DateTime utcNow)
{
    public FakeClock() : this(Dates.Now) { }

    public DateTime UtcNow { get; private set; } = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);

    public FakeClock Advance(TimeSpan by)
    {
        UtcNow = UtcNow.Add(by);
        return this;
    }
}

public static class Dates
{
    public static readonly DateTime Now = Utc(2026, 10, 15);
    public static readonly DateTime Far = Utc(2099, 1, 1);
    public static readonly DateTime Past = Utc(2020, 1, 1);

    public static DateTime Utc(int year, int month, int day, int hour = 0, int minute = 0, int second = 0)
        => new(year, month, day, hour, minute, second, DateTimeKind.Utc);
}
