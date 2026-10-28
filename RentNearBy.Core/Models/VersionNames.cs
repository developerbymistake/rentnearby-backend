namespace RentNearBy.Core.Models;

public static class VersionNames
{
    private const int MinSegments = 2;
    private const int MaxSegments = 4;
    private const int MaxSegmentLength = 5;

    public static bool IsValid(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;

        var segments = value.Split('.');
        if (segments.Length < MinSegments || segments.Length > MaxSegments) return false;

        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment.Length > MaxSegmentLength) return false;
            foreach (var c in segment)
                if (!char.IsAsciiDigit(c)) return false;
        }
        return true;
    }
}
