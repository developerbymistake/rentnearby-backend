namespace RentNearBy.Core.Models;

// Bigha is a state unit; AreaSqft (the sort key) is computed from the plot district's state.
public static class BighaArea
{
    public const decimal DefaultBighaSqft = 27000m;
    public const decimal AcreSqft = 43560m;
    public const decimal NaliSqft = 2152.78m;

    private static readonly Dictionary<string, decimal> BighaSqftByState = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Uttarakhand"] = 6804m,
        ["Himachal Pradesh"] = 8712m,
        ["Rajasthan"] = 27225m,
        ["Uttar Pradesh"] = 27000m,
    };

    public static IReadOnlyDictionary<string, decimal> StateFactors => BighaSqftByState;

    public static decimal BighaSqft(string? stateName)
        => stateName != null && BighaSqftByState.TryGetValue(stateName.Trim(), out var f) ? f : DefaultBighaSqft;

    public static decimal ToSqft(decimal value, string unit, string? stateName) => unit switch
    {
        "sqft" => value,
        "bigha" => value * BighaSqft(stateName),
        "acre" => value * AcreSqft,
        "nali" => value * NaliSqft,
        _ => value,
    };
}
