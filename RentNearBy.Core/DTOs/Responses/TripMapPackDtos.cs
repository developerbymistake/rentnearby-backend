using RentNearBy.Core.Models;

namespace RentNearBy.Core.DTOs.Responses;

public class TripMapPackInfoDto
{
    public string StateId { get; set; } = string.Empty;
    public string StateName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public int Count { get; set; }
    public double[] Bbox { get; set; } = [];
    // Relative to /api/v1.
    public string Path { get; set; } = string.Empty;

    public static TripMapPackInfoDto From(TripMapPackInfo info) => new()
    {
        StateId = info.StateId.ToString("D"),
        StateName = info.StateName,
        Version = info.Version,
        Count = info.Count,
        Bbox = info.Bbox.ToArray(),
        Path = TripMapPackKeys.PathOf(info.StateId, info.Version),
    };
}
