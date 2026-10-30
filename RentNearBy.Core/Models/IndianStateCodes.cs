namespace RentNearBy.Core.Models;

// RTO state/UT codes: the 36 current ones plus legacy UA, OR, DN and TG (Telangana 2024) still printed on valid papers.
public static class IndianStateCodes
{
    public static readonly string[] All =
    [
        "AN", "AP", "AR", "AS", "BR", "CG", "CH", "DD", "DL", "GA", "GJ", "HP", "HR", "JH", "JK", "KA", "KL", "LA",
        "LD", "MH", "ML", "MN", "MP", "MZ", "NL", "OD", "PB", "PY", "RJ", "SK", "TN", "TR", "TS", "UK", "UP", "WB",
        "UA", "OR", "DN", "TG",
    ];

    private static readonly HashSet<string> Set = new(All, StringComparer.Ordinal);

    public static bool Contains(string code) => Set.Contains(code);
}
