using System.Globalization;
using System.Text.Json;
using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.Trips;

public class OsrmClientParseTests
{
    [Fact]
    public void ParseTable_ConvertsUnits_NullCellIsEstimate()
    {
        var body = Json.Of(new
        {
            code = "Ok",
            durations = new[] { new double?[] { 0, 600 }, new double?[] { null, 0 } },
            distances = new[] { new double?[] { 0, 12340 }, new double?[] { null, 0 } },
        });

        var matrix = OsrmClient.ParseTable(body, 2);

        Assert.NotNull(matrix);
        Assert.Equal(10, matrix.Minutes![0, 1]);
        Assert.Equal(12.3, matrix.Km![0, 1]);
        Assert.False(matrix.IsEstimate[0, 1]);
        Assert.True(matrix.IsEstimate[1, 0]);
    }

    [Fact]
    public void ParseTable_RoundsMinutesUp()
    {
        var body = Json.Of(new
        {
            code = "Ok",
            durations = new[] { new double?[] { 0, 61 }, new double?[] { 61, 0 } },
            distances = new[] { new double?[] { 0, 900 }, new double?[] { 900, 0 } },
        });

        var matrix = OsrmClient.ParseTable(body, 2);

        Assert.Equal(2, matrix!.Minutes![0, 1]);
        Assert.Equal(0.9, matrix.Km![1, 0]);
    }

    [Fact]
    public void ParseTable_MissingDistances_AllEstimate()
    {
        var body = Json.Of(new { code = "Ok", durations = new[] { new double?[] { 0, 60 }, new double?[] { 60, 0 } } });

        var matrix = OsrmClient.ParseTable(body, 2);

        Assert.True(matrix!.IsEstimate[0, 1]);
        Assert.True(matrix.IsEstimate[1, 0]);
    }

    [Theory]
    [InlineData("{\"code\":\"InvalidQuery\",\"message\":\"bad\"}")]
    [InlineData("{\"code\":\"Ok\",\"durations\":[[0,1]]}")]
    [InlineData("not json")]
    public void ParseTable_NotOkOrMalformed_Null(string body)
        => Assert.Null(OsrmClient.ParseTable(body, 2));

    [Fact]
    public void ParseRoute_ExtractsGeometry()
    {
        var body = Json.Of(new
        {
            code = "Ok",
            routes = new[]
            {
                new
                {
                    geometry = new { type = "LineString", coordinates = new[] { new[] { 79.4542, 29.3919 }, new[] { 79.47, 29.4 } } },
                    duration = 900.0,
                    distance = 5000.0,
                },
            },
        });

        var route = OsrmClient.ParseRoute(body);

        Assert.NotNull(route);
        Assert.Equal(15, route.Minutes);
        Assert.Equal(5.0, route.Km);
        Assert.Equal("LineString", route.Geometry.GetProperty("type").GetString());
        Assert.Equal(2, route.Geometry.GetProperty("coordinates").GetArrayLength());
        Assert.Contains("\"coordinates\"", JsonSerializer.Serialize(route.Geometry));
    }

    [Theory]
    [InlineData("{\"code\":\"NoRoute\",\"message\":\"Impossible route\"}")]
    [InlineData("{\"code\":\"Ok\",\"routes\":[]}")]
    [InlineData("")]
    public void ParseRoute_NoRoute_Null(string body)
        => Assert.Null(OsrmClient.ParseRoute(body));

    [Fact]
    public void Coordinates_LonLatInvariant()
    {
        var previous = CultureInfo.CurrentCulture;
        var comma = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        comma.NumberFormat.NumberDecimalSeparator = ",";
        CultureInfo.CurrentCulture = comma;
        try
        {
            Assert.Equal("79.4542,29.3919;79.47,29.4", OsrmClient.Coordinates(new[] { (29.3919, 79.4542), (29.4, 79.47) }));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void ParseTableBlock_ReadsRectangle_NullCellStaysNull()
    {
        var body = Json.Of(new
        {
            code = "Ok",
            durations = new[] { new double?[] { 600, null, 61 } },
            distances = new[] { new double?[] { 12340, null, 500 } },
        });

        var table = OsrmClient.ParseTableBlock(body, 1, 3);

        Assert.NotNull(table);
        Assert.Equal(600, table.Seconds[0, 0]);
        Assert.Null(table.Seconds[0, 1]);
        Assert.Equal(500, table.Metres[0, 2]);
    }

    [Theory]
    [InlineData("{\"code\":\"TooBig\"}")]
    [InlineData("{\"code\":\"Ok\",\"durations\":[[1,2]]}")]
    public void ParseTableBlock_BadBody_IsNull(string body)
        => Assert.Null(OsrmClient.ParseTableBlock(body, 2, 2));
}
