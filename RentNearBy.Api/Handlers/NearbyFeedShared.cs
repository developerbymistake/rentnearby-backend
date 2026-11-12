using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Models;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

public static class NearbyFeedShared
{
    // BadRequest result, or null when the args are valid.
    public static IResult? TryParse(double? lat, double? lng, int? pageSize, string? pageToken, out NearbyArgs args)
    {
        args = new NearbyArgs(0, 0, NearbyRules.DefaultPageSize, null);
        if (!NearbyRules.ValidLatLng(lat, lng)) return BadRequestResponse("Invalid location");
        NearbyToken? after = null;
        if (!string.IsNullOrEmpty(pageToken) && !NearbyToken.TryDecode(pageToken, out after))
            return BadRequestResponse("Invalid page token");
        var size = Math.Clamp(pageSize ?? NearbyRules.DefaultPageSize, 1, NearbyRules.MaxPageSize);
        args = new NearbyArgs(lat!.Value, lng!.Value, size, after);
        return null;
    }

    public static (IReadOnlyList<NearbyRow> Page, bool HasMore) Split(IReadOnlyList<NearbyRow> rowsPlusOne, int pageSize)
        => rowsPlusOne.Count > pageSize ? (rowsPlusOne.Take(pageSize).ToList(), true) : (rowsPlusOne, false);

    public static NearbyPageDto<T> Compose<T>(NearbyHeaderDto header, IReadOnlyList<T> featured, IReadOnlyList<T> items,
        NearbyRow? lastRow, bool hasMore)
        => new()
        {
            Header = header,
            Featured = featured.ToList(),
            Items = items.ToList(),
            HasMore = hasMore,
            NextToken = hasMore && lastRow != null ? new NearbyToken(lastRow.DistanceMeters, lastRow.Id).Encode() : null,
        };
}
