using RentNearBy.Core.DTOs.Responses;

namespace RentNearBy.Core.Models;

// Per-district counts keyed by the district id (names repeat across states: Hamirpur HP and UP), largest first.
public static class AdminDistrictCounts
{
    public static List<AdminDistrictCountDto> Build(
        IEnumerable<(Guid DistrictId, int Count)> counts,
        IReadOnlyDictionary<Guid, (string Name, string StateName)> districts)
        => counts
            .GroupBy(c => c.DistrictId)
            .Select(g =>
            {
                var known = districts.TryGetValue(g.Key, out var d);
                return new AdminDistrictCountDto
                {
                    DistrictId = g.Key,
                    DistrictName = known ? d.Name : string.Empty,
                    StateName = known ? d.StateName : string.Empty,
                    Count = g.Sum(c => c.Count),
                };
            })
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.DistrictName, StringComparer.Ordinal)
            .ThenBy(x => x.StateName, StringComparer.Ordinal)
            .ThenBy(x => x.DistrictId)
            .ToList();
}
