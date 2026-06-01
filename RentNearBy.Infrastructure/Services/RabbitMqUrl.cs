using Microsoft.Extensions.Configuration;

namespace RentNearBy.Infrastructure.Services;

internal static class RabbitMqUrl
{
    internal static string Build(IConfiguration cfg)
    {
        // Priority 1: full URL (RABBITMQ_URL)
        var url = cfg["RABBITMQ_URL"];
        if (!string.IsNullOrWhiteSpace(url)) return url;

        // Priority 2: Coolify-style separate credential variables
        var user = cfg["SERVICE_USER_RABBITMQ"];
        var pass = cfg["SERVICE_PASSWORD_RABBITMQ"];
        var host = cfg["RABBITMQ_HOST"] ?? "rabbitmq";
        var port = cfg["RABBITMQ_PORT"] ?? "5672";

        if (!string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(pass))
            return $"amqp://{Uri.EscapeDataString(user)}:{Uri.EscapeDataString(pass)}@{host}:{port}";

        throw new InvalidOperationException(
            "RabbitMQ not configured. Set RABBITMQ_URL or SERVICE_USER_RABBITMQ + SERVICE_PASSWORD_RABBITMQ.");
    }
}
