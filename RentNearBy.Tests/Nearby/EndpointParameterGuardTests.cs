using System.Reflection;
using Microsoft.AspNetCore.Http;
using RentNearBy.Api.Handlers;

namespace RentNearBy.Tests.Nearby;

public class EndpointParameterGuardTests
{
    [Fact]
    public void Handlers_never_take_a_repository_interface_as_a_parameter()
    {
        var bad = typeof(TaxiFeedHandlers).Assembly.GetTypes()
            .Where(t => t.Namespace == "RentNearBy.Api.Handlers" && t.IsAbstract && t.IsSealed)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(m => typeof(Task<IResult>).IsAssignableFrom(m.ReturnType))
            .SelectMany(m => m.GetParameters().Select(p => (m, p)))
            .Where(x => x.p.ParameterType.IsInterface && x.p.ParameterType.Name.EndsWith("Repository")
                && x.p.ParameterType.Name != "ISavedJourneyRepository" && x.p.ParameterType.Name != "IPlaySubscriptionRepository")
            .Select(x => $"{x.m.DeclaringType!.Name}.{x.m.Name}({x.p.ParameterType.Name} {x.p.Name})")
            .ToList();
        Assert.True(bad.Count == 0, "Repositories come from IUnitOfWork, not DI parameters (minimal API infers a body): " + string.Join(", ", bad));
    }
}
