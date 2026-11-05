using NetTopologySuite.Geometries;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Tests.Districts;

public class DistrictNameResolverTests
{
    private static readonly GeometryFactory Factory = new(new PrecisionModel(), 4326);

    private static Polygon Box(double minLng, double minLat, double maxLng, double maxLat)
        => Factory.CreatePolygon([
            new Coordinate(minLng, minLat), new Coordinate(maxLng, minLat), new Coordinate(maxLng, maxLat),
            new Coordinate(minLng, maxLat), new Coordinate(minLng, minLat)]);

    private static readonly Guid BilaspurHp = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid BilaspurCg = Guid.Parse("00000000-0000-0000-0000-0000000000a2");
    private static readonly Guid Nainital = Guid.Parse("00000000-0000-0000-0000-0000000000b1");

    // Finding 45: Bilaspur is a district of Himachal Pradesh and of Chhattisgarh.
    private static DistrictNameResolver Resolver() => new([
        new(BilaspurHp, "Bilaspur", Box(76.5, 31.2, 76.9, 31.6)),
        new(BilaspurCg, "Bilaspur", Box(81.8, 21.8, 82.4, 22.4)),
        new(Nainital, "Nainital", null),
    ]);

    [Fact]
    public void AmbiguousName_ResolvedByPoint()
    {
        var resolver = Resolver();
        Assert.True(resolver.IsAmbiguous("bilaspur"));
        Assert.Equal(BilaspurHp, resolver.Resolve("Bilaspur", 31.44, 76.71));
        Assert.Equal(BilaspurHp, resolver.Resolve("Bilaspur", 31.33, 76.76));
        Assert.Equal(BilaspurCg, resolver.Resolve(" BILASPUR ", 22.08, 82.15));
    }

    [Fact]
    public void AmbiguousName_PointOutsideBoth_NearestBoundary()
        => Assert.Equal(BilaspurHp, Resolver().Resolve("Bilaspur", 31.7, 76.95));

    [Fact]
    public void UniqueName_NoGeometryNeeded()
        => Assert.Equal(Nainital, Resolver().Resolve("nainital", double.NaN, double.NaN));

    [Fact]
    public void UnknownName_Null()
        => Assert.Null(Resolver().Resolve("Atlantis", 31.44, 76.71));

    [Fact]
    public void AmbiguousName_NoBoundaries_Null()
    {
        var resolver = new DistrictNameResolver([new(BilaspurHp, "Bilaspur", null), new(BilaspurCg, "Bilaspur", null)]);
        Assert.Null(resolver.Resolve("Bilaspur", 31.44, 76.71));
    }

    [Fact]
    public void AmbiguousKeys_OnlyNamesWithTwoIds()
        => Assert.Equal(new[] { "bilaspur" }, DistrictNameResolver.AmbiguousKeys([
            (BilaspurHp, "Bilaspur"), (BilaspurCg, "bilaspur "), (Nainital, "Nainital"), (Nainital, "Nainital")]));

    // Same-name villages of one district are kept unless they are the same place (compact name within 2 km).
    [Fact]
    public void SeedDedupe_SameNameFarApart_BothKept_NearbySpelling_Skipped()
    {
        var dedupe = new DataSeeder.CitySeedDedupe();
        Assert.True(dedupe.TryAdd(Nainital, "Rampur", 29.30, 79.50));
        Assert.True(dedupe.TryAdd(Nainital, "Rampur", 29.10, 79.60));
        Assert.False(dedupe.TryAdd(Nainital, "rampur", 29.301, 79.501));
        Assert.True(dedupe.TryAdd(Nainital, "Bhimtal", 29.3460, 79.5600));
        Assert.False(dedupe.TryAdd(Nainital, "Bhim Tal", 29.3470, 79.5610));
        Assert.True(dedupe.TryAdd(BilaspurHp, "Rampur", 29.30, 79.50));
    }
}
