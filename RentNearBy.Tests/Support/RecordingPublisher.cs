namespace RentNearBy.Tests.Support;

// Hand-written IRabbitMqPublisher: records what production code publishes, or fails like a broker outage.
public sealed class RecordingPublisher(Exception? failWith = null) : IRabbitMqPublisher
{
    public List<(string Queue, string Message)> Published { get; } = [];

    public Task PublishAsync(string queue, string message)
    {
        if (failWith != null) throw failWith;
        Published.Add((queue, message));
        return Task.CompletedTask;
    }
}
