namespace RentNearBy.Core.Interfaces;

public interface IRabbitMqPublisher
{
    Task PublishAsync(string queue, string message);
}
