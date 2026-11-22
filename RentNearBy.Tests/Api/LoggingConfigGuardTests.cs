using System.Text.Json;
using RentNearBy.Tests.Trips;

namespace RentNearBy.Tests.Api;

// FB-10: production logs never carry every SQL statement (EF Core category at Warning).
public class LoggingConfigGuardTests
{
    [Fact]
    public void Example_settings_log_ef_core_at_warning()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(CompletionCounterGuardTests.Root(), "RentNearBy.Api", "appsettings.example.json")),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var level = doc.RootElement.GetProperty("Logging").GetProperty("LogLevel");
        Assert.Equal("Warning", level.GetProperty("Microsoft.EntityFrameworkCore").GetString());
    }
}
