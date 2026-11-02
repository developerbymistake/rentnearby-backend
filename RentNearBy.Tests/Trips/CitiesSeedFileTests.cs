using System.Text.Json;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Tests.Trips;

public class CitiesSeedFileTests
{
    private static List<string> Names()
    {
        using var stream = typeof(DataSeeder).Assembly.GetManifestResourceStream("RentNearBy.Infrastructure.Data.cities.json");
        Assert.NotNull(stream);
        using var doc = JsonDocument.Parse(stream);
        return doc.RootElement.EnumerateArray().Select(e => e.GetProperty("name").GetString() ?? "").ToList();
    }

    private static bool HasHan(string name)
        => name.Any(ch => ch is (>= '一' and <= '鿿') or (>= '㐀' and <= '䶿') or (>= '豈' and <= '﫿'));

    [Fact]
    public void Embedded_IsClean()
    {
        var names = Names();

        Assert.Equal(182_730, names.Count);
        Assert.DoesNotContain(names, HasHan);
    }
}
