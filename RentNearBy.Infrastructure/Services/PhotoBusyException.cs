namespace RentNearBy.Infrastructure.Services;

// The shared decode gate stayed full past its wait: the server answers 503 PhotoBusy with Retry-After instead of queueing forever.
public sealed class PhotoBusyException : Exception
{
    public const string UserMessage = "Busy right now. Try again in a few seconds.";
    public const int RetryAfterSeconds = 5;

    public PhotoBusyException() : base(UserMessage) { }
}
