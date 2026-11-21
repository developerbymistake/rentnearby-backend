namespace RentNearBy.Tests.Owner;

// Marks the one enforcement test of a plan key (contract 3.2 / 13.16). PlanKeyCatalogTests reads it: every key needs one.
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class PlanKeyTestAttribute(string key) : Attribute
{
    public string Key { get; } = key;
}
