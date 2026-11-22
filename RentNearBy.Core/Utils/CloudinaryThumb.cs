namespace RentNearBy.Core.Utils;

public static class CloudinaryThumb
{
    public const string CityCover = "c_fill,w_480,h_360,q_auto,f_auto";

    // Stay nearby/nearest map sheets.
    public const string StayMap = "c_fill,w_480,h_360,q_auto,f_auto";

    // Round taxi picture on list cards (136 px circle at 2x and a little spare).
    public const string TaxiFace = "c_fill,g_auto,w_320,h_320,q_auto,f_auto";

    // Spot photos: the only five sizes (same strings as the user app SpotPhotoUrl).
    public const string SpotMarker = "c_fill,w_96,h_96,q_auto,f_auto";
    public const string SpotSquare = "c_fill,g_auto,w_320,h_320,q_auto,f_auto";
    public const string SpotCard = "c_fill,g_auto,w_720,h_400,q_auto,f_auto";
    public const string SpotHero = "c_fill,g_auto,w_1080,h_640,q_auto,f_auto";
    public const string SpotFull = "c_limit,w_1280,q_auto,f_auto";

    public static readonly IReadOnlyList<string> SpotSizes = [SpotMarker, SpotSquare, SpotCard, SpotHero, SpotFull];

    private const string UploadMarker = "/image/upload/";

    // Cloudinary image URLs get the transformation right after /image/upload/; anything else is returned unchanged.
    public static string? Apply(string? url, string transformation = CityCover)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var trimmed = url.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || !uri.Host.EndsWith("cloudinary.com", StringComparison.OrdinalIgnoreCase))
            return trimmed;

        var at = trimmed.IndexOf(UploadMarker, StringComparison.Ordinal);
        if (at < 0) return trimmed;

        var tail = at + UploadMarker.Length;
        if (string.CompareOrdinal(trimmed, tail, transformation, 0, transformation.Length) == 0) return trimmed;
        return string.Concat(trimmed.AsSpan(0, tail), transformation, "/", trimmed.AsSpan(tail));
    }
}
