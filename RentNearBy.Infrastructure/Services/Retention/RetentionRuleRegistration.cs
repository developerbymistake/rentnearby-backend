using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace RentNearBy.Infrastructure.Services.Retention;

// Finds every concrete IRetentionRule and IRetentionRuleSource in an assembly (sorted by full name, so the order is stable).
public static class RetentionRuleRegistration
{
    public static IReadOnlyList<IRetentionRule> Discover(Assembly assembly)
    {
        var types = assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false }
                        && t != typeof(DeclarativeRetentionRule)
                        && t.GetConstructor(Type.EmptyTypes) is { IsPublic: true })
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();
        var rules = new List<IRetentionRule>();
        foreach (var t in types)
        {
            if (typeof(IRetentionRule).IsAssignableFrom(t)) rules.Add((IRetentionRule)Activator.CreateInstance(t)!);
            if (typeof(IRetentionRuleSource).IsAssignableFrom(t)) rules.AddRange(((IRetentionRuleSource)Activator.CreateInstance(t)!).Create());
        }
        return rules;
    }

    public static IServiceCollection AddRetentionRules(this IServiceCollection services, Assembly assembly)
    {
        foreach (var rule in Discover(assembly)) services.AddSingleton(typeof(IRetentionRule), rule);
        return services;
    }
}
