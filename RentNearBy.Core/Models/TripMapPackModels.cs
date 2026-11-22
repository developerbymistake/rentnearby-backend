namespace RentNearBy.Core.Models;

public static class TripMapPackKeys
{
    public const string Index = "trip:packs:index";
    public const int VersionLength = 16;
    public const int MaxRawBytes = 2 * 1024 * 1024;
    public static readonly TimeSpan Ttl = TimeSpan.FromDays(7);

    public static string Pack(Guid stateId, string version) => $"trip:pack:{stateId:N}:{version}";
    public static string Lock(Guid stateId, string version) => $"{Pack(stateId, version)}:lock";

    // Lowercase hex only, so a version is safe in a path, a Redis key and an ETag.
    public static bool IsVersion(string? value)
    {
        if (value == null || value.Length != VersionLength) return false;
        foreach (var c in value)
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f'))) return false;
        return true;
    }

    public static string PathOf(Guid stateId, string version) => $"/trips/map-packs/{stateId:D}/{version}";
}

public static class MapPackErrorTypes
{
    public const string MapPackNotFound = "MapPackNotFound";
    public const string MapPackUnavailable = "MapPackUnavailable";
}

// One item of the Atlas GET /map-packs list.
public sealed class TripMapPackInfo
{
    public Guid StateId { get; set; }
    public string StateName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public int Count { get; set; }
    public double[] Bbox { get; set; } = [];
}

// Redis value of TripMapPackKeys.Index and the in-process copy.
public sealed class TripMapPackIndex
{
    public DateTime FetchedAt { get; set; }
    public List<TripMapPackInfo> Packs { get; set; } = new();
}

// Gzip of the Atlas pack body; never mutated after it is built.
public sealed record TripMapPackBytes(string Version, byte[] Gzip);
