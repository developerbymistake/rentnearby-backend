using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RentNearBy.Api.Handlers;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Tests.TaxiRequests;

// DELETE /trips/{id} binds the scoped context from DI, so the plan lock guard always has it; a direct call without it throws.
public class TripDeleteBindingTests
{
    [Fact]
    public async Task Endpoint_binding_gives_delete_a_non_null_context()
    {
        var ctx = await InvokeAsync(registerContext: true);
        Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Delete_without_a_context_throws_instead_of_skipping_the_guard()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => InvokeAsync(registerContext: false));
        await Assert.ThrowsAsync<ArgumentNullException>(() => TripHandlers.Delete(
            Guid.NewGuid(), new ClaimsPrincipal(new ClaimsIdentity()), Fake<IUnitOfWork>(), Fake<ITripEnquiryClosure>(),
            Fake<ISavedJourneyRepository>()));
    }

    private static async Task<HttpContext> InvokeAsync(bool registerContext)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (registerContext)
            services.AddDbContextPool<ApplicationDbContext>(o =>
                o.UseNpgsql("Host=localhost;Database=binding_only", n => n.UseNetTopologySuite()));
        services.AddScoped(_ => Fake<IUnitOfWork>());
        services.AddScoped(_ => Fake<ITripEnquiryClosure>());
        services.AddScoped(_ => Fake<ISavedJourneyRepository>());
        var provider = services.BuildServiceProvider();

        var factory = RequestDelegateFactory.Create(TripHandlers.Delete, new RequestDelegateFactoryOptions { ServiceProvider = provider });
        var scope = provider.CreateScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        http.Request.Method = HttpMethods.Delete;
        http.Request.RouteValues["id"] = Guid.NewGuid().ToString();
        http.Response.Body = new MemoryStream();
        await factory.RequestDelegate(http);
        return http;
    }

    private static T Fake<T>() where T : class => DispatchProxy.Create<T, ThrowingProxy>();

    public class ThrowingProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => throw new InvalidOperationException($"{targetMethod?.Name} must not be called before the auth check");
    }
}
