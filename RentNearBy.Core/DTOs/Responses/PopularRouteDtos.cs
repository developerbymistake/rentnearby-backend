namespace RentNearBy.Core.DTOs.Responses;

// Contract 1.5 P1 item. minFare / taxiCount are computed for IST today.
public sealed record PopularRoutePublicDto(
    Guid Id, long FromCityKey, string FromName, double FromLat, double FromLng,
    long ToCityKey, string ToName, double ToLat, double ToLng, int? MinFare, int TaxiCount);

public sealed record PopularRoutesPublicDto(string Date, IReadOnlyList<PopularRoutePublicDto> Items);

// Contract 1.6 admin item. No price, photo or counts.
public sealed record PopularRouteAdminDto(Guid Id, long FromCityKey, string FromCityName, long ToCityKey, string ToCityName, int SortOrder, bool IsActive);

public sealed record PopularRoutesAdminDto(IReadOnlyList<PopularRouteAdminDto> Items);

public sealed class PopularRouteCreateRequest
{
    public long? FromCityKey { get; set; }
    public long? ToCityKey { get; set; }
}

public sealed class PopularRouteActiveRequest
{
    public bool? IsActive { get; set; }
}

public sealed class PopularRouteOrderRequest
{
    public List<Guid>? Ids { get; set; }
}
