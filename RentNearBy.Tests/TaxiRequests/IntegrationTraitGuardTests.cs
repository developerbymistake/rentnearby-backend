using System.Reflection;
using RentNearBy.Tests.Integration;

namespace RentNearBy.Tests.TaxiRequests;

// Integration classes need Category=Integration so the default run (-trait- "Category=Integration") skips them.
public class IntegrationTraitGuardTests
{
    private const string Namespace = "RentNearBy.Tests.TaxiRequests";

    internal static bool IsIntegrationClass(Type t)
        => t.Name.EndsWith("IntegrationTests", StringComparison.Ordinal)
           || typeof(IntegrationTestBase).IsAssignableFrom(t)
           || t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
               .Any(m => m.GetCustomAttributes(typeof(IntegrationFactAttribute), true).Length > 0
                         || m.GetCustomAttributes(typeof(IntegrationTheoryAttribute), true).Length > 0);

    internal static bool HasTrait(Type t)
        => t.GetCustomAttributes<TraitAttribute>(true).Any(a => a.Name == "Category" && a.Value == "Integration");

    internal static IEnumerable<string> Violations(IEnumerable<Type> types)
        => types.Where(t => IsIntegrationClass(t) && !HasTrait(t)).Select(t => t.FullName!);

    [Fact]
    public void Every_integration_test_class_in_this_namespace_has_the_category_trait()
    {
        var types = typeof(IntegrationTraitGuardTests).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsNested && !t.IsAbstract && t.Namespace is { } ns && (ns == Namespace || ns.StartsWith(Namespace + ".", StringComparison.Ordinal)));
        Assert.Empty(Violations(types));
    }

    private sealed class FakeIntegrationTests { }

    [Trait("Category", "Integration")]
    private sealed class FakeTaggedIntegrationTests { }

    private sealed class FakePlainTests { }

    [Fact]
    public void The_guard_flags_an_untagged_integration_class_and_accepts_a_tagged_one()
    {
        var found = Violations([typeof(FakeIntegrationTests), typeof(FakeTaggedIntegrationTests), typeof(FakePlainTests)]).ToArray();
        Assert.Equal([typeof(FakeIntegrationTests).FullName!], found);
    }
}
