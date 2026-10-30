using Microsoft.Extensions.Configuration;
using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.Infrastructure;

public class RabbitMqUrlTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    [Fact]
    public void Build_FullUrl_WinsOverCredentials()
        => Assert.Equal("amqp://a:b@mq:5672/vh", RabbitMqUrl.Build(Config(
            ("RABBITMQ_URL", "amqp://a:b@mq:5672/vh"),
            ("SERVICE_USER_RABBITMQ", "u"),
            ("SERVICE_PASSWORD_RABBITMQ", "p"))));

    [Fact]
    public void Build_UserAndPassword_DefaultHostAndPort()
        => Assert.Equal("amqp://u:p@rabbitmq:5672", RabbitMqUrl.Build(Config(
            ("SERVICE_USER_RABBITMQ", "u"),
            ("SERVICE_PASSWORD_RABBITMQ", "p"))));

    [Fact]
    public void Build_WhitespaceUrl_FallsBackToCredentials()
        => Assert.Equal("amqp://u:p@rabbitmq:5672", RabbitMqUrl.Build(Config(
            ("RABBITMQ_URL", "  "),
            ("SERVICE_USER_RABBITMQ", "u"),
            ("SERVICE_PASSWORD_RABBITMQ", "p"))));

    [Fact]
    public void Build_SpecialCharacters_Escaped()
        => Assert.Equal("amqp://us%3Aer:p%40ss%2Fw@rabbitmq:5672", RabbitMqUrl.Build(Config(
            ("SERVICE_USER_RABBITMQ", "us:er"),
            ("SERVICE_PASSWORD_RABBITMQ", "p@ss/w"))));

    [Fact]
    public void Build_HostAndPortOverrides()
        => Assert.Equal("amqp://u:p@broker.internal:5673", RabbitMqUrl.Build(Config(
            ("SERVICE_USER_RABBITMQ", "u"),
            ("SERVICE_PASSWORD_RABBITMQ", "p"),
            ("RABBITMQ_HOST", "broker.internal"),
            ("RABBITMQ_PORT", "5673"))));

    [Fact]
    public void Build_NothingConfigured_Throws()
        => Assert.Throws<InvalidOperationException>(() => RabbitMqUrl.Build(Config()));

    [Theory]
    [InlineData("u", null)]
    [InlineData(null, "p")]
    [InlineData("u", " ")]
    public void Build_PartialCredentials_Throws(string? user, string? password)
        => Assert.Throws<InvalidOperationException>(() => RabbitMqUrl.Build(Config(
            ("SERVICE_USER_RABBITMQ", user),
            ("SERVICE_PASSWORD_RABBITMQ", password))));
}
