using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;

namespace RentNearBy.Tests.Infrastructure;

public class PgAdvisoryLockTests(TestDatabase db) : IntegrationTestBase(db)
{
    private static IConfiguration Config(params (string Key, string? Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    [Fact]
    public void Lock_connection_string_keeps_the_password_from_DATABASE_URL()
    {
        var cs = DbLockConnectionString.Resolve(Config(("DATABASE_URL", "Host=h;Database=d;Username=u;Password=secret")));
        Assert.Equal("secret", new NpgsqlConnectionStringBuilder(cs).Password);
    }

    [Fact]
    public void Lock_connection_string_falls_back_to_DefaultConnection_with_password()
    {
        var cs = DbLockConnectionString.Resolve(Config(("ConnectionStrings:DefaultConnection", "Host=h;Database=d;Username=u;Password=secret")));
        Assert.Equal("secret", new NpgsqlConnectionStringBuilder(cs).Password);
    }

    [Fact]
    public void Lock_connection_string_requires_a_configured_value()
        => Assert.Throws<InvalidOperationException>(() => DbLockConnectionString.Resolve(Config()));

    [IntegrationFact]
    public async Task Lock_opens_with_the_configured_string_after_the_context_stripped_the_password()
    {
        var ct = TestContext.Current.CancellationToken;
        var configured = Environment.GetEnvironmentVariable(TestDatabase.EnvVar)!;
        await using var ctx = CreateContext();
        await ctx.Database.CanConnectAsync(ct);
        await ctx.Database.ExecuteSqlRawAsync("SELECT 1", ct);

        await using var first = await PgAdvisoryLock.TryAcquireAsync(configured, "test.lock.password", ct);
        Assert.NotNull(first);
        await using var second = await PgAdvisoryLock.TryAcquireAsync(configured, "test.lock.password", ct);
        Assert.Null(second);
    }
}
