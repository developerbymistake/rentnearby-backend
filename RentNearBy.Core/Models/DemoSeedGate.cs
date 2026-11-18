namespace RentNearBy.Core.Models;

// The Nainital demo seeder runs in Development, or elsewhere only when DemoSeed:Enabled is "true"; never in Production.
// An unset environment name counts as Production (the ASP.NET default).
public static class DemoSeedGate
{
    public const string ConfigKey = "DemoSeed:Enabled";

    public static bool ShouldRun(string? environmentName, string? configValue)
    {
        if (string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.IsNullOrWhiteSpace(environmentName)
            || string.Equals(environmentName, "Production", StringComparison.OrdinalIgnoreCase)) return false;
        return string.Equals(configValue?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }
}
