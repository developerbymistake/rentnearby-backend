using Microsoft.Extensions.Configuration;

namespace RentNearBy.Infrastructure.Services;

// The configured connection string with its password. DbContext.Database.GetConnectionString() cannot be used for a
// dedicated connection: Npgsql drops the password from it once the context has opened a connection.
public sealed record DbLockConnectionString(string Value)
{
    public static string Resolve(IConfiguration configuration)
        => configuration["DATABASE_URL"]
            ?? configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("DATABASE_URL not configured");
}
