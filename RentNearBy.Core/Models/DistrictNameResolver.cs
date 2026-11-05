using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;

namespace RentNearBy.Core.Models;

// District of a seed row: by name when the name is unique in India, else by the point (prepared polygon
// Covers over the same-name districts only, nearest boundary when the point is in none). Mirrors Atlas DistrictResolver.
public sealed class DistrictNameResolver
{
    public sealed record Candidate(Guid Id, string Name, Geometry? Boundary);

    private sealed record Shape(Guid Id, Geometry Boundary, IPreparedGeometry Prepared);

    private readonly Dictionary<string, Guid> _unique = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Shape>> _ambiguous = new(StringComparer.Ordinal);

    public DistrictNameResolver(IEnumerable<Candidate> districts)
    {
        ArgumentNullException.ThrowIfNull(districts);
        foreach (var group in districts.Where(d => !string.IsNullOrWhiteSpace(d.Name)).GroupBy(d => Key(d.Name), StringComparer.Ordinal))
        {
            var items = group.DistinctBy(d => d.Id).ToList();
            if (items.Count == 1)
            {
                _unique[group.Key] = items[0].Id;
                continue;
            }
            _ambiguous[group.Key] = items
                .Where(d => d.Boundary is { IsEmpty: false })
                .Select(d => new Shape(d.Id, d.Boundary!, PreparedGeometryFactory.Prepare(d.Boundary!)))
                .ToList();
        }
    }

    public static string Key(string name) => name.Trim().ToLowerInvariant();

    public static IReadOnlySet<string> AmbiguousKeys(IEnumerable<(Guid Id, string Name)> districts)
        => districts
            .Where(d => !string.IsNullOrWhiteSpace(d.Name))
            .GroupBy(d => Key(d.Name), StringComparer.Ordinal)
            .Where(g => g.Select(d => d.Id).Distinct().Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);

    public bool IsAmbiguous(string districtName) => _ambiguous.ContainsKey(Key(districtName));

    public Guid? Resolve(string districtName, double latitude, double longitude)
    {
        if (string.IsNullOrWhiteSpace(districtName)) return null;
        var key = Key(districtName);
        if (_unique.TryGetValue(key, out var id)) return id;
        if (!_ambiguous.TryGetValue(key, out var shapes) || shapes.Count == 0) return null;
        if (double.IsNaN(latitude) || double.IsNaN(longitude)) return null;

        var point = new Point(longitude, latitude) { SRID = 4326 };
        foreach (var shape in shapes)
            if (shape.Prepared.Covers(point)) return shape.Id;

        return shapes.MinBy(s => s.Boundary.Distance(point))!.Id;
    }
}
