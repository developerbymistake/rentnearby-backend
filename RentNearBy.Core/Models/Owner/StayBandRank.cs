using RentNearBy.Core.DTOs.Responses;

namespace RentNearBy.Core.Models.Owner;

// T7: the order of the main Stays list (radius reads) inside the result: 5 km distance band, then paid rank, then distance.
// A special price or a block never moves a stay. The trip city list keeps StayCityRank.
public static class StayBandRank
{
    public static int Band(double distanceKm) => distanceKm <= 0 || double.IsNaN(distanceKm) ? 0 : (int)Math.Floor(distanceKm / OwnerLimits.RankBandKm);

    public static List<StayNearbyDto> Order(IEnumerable<StayNearbyDto> items)
        => items
            .Select((s, i) => (Stay: s, Index: i))
            .OrderBy(x => Band(x.Stay.DistanceKm))
            .ThenByDescending(x => x.Stay.PriorityRank)
            .ThenBy(x => x.Stay.DistanceKm)
            .ThenBy(x => x.Index)
            .Select(x => x.Stay)
            .ToList();
}
