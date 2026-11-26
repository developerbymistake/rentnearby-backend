using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RentNearBy.Api.Extensions;
using static RentNearBy.Tests.SeekerOnly.SeekerRouteTable;

namespace RentNearBy.Tests.SeekerOnly;

// BE-T3 (contract 2.3): a failed SeekerOnly requirement answers 403 with error.type SeekerOnly and the exact message; every
// other forbidden result keeps the framework answer (empty 403 through ForbidAsync); a success runs the endpoint.
public class SeekerForbiddenResultHandlerTests
{
    private sealed class RecordingAuthentication : IAuthenticationService
    {
        public int Forbids { get; private set; }
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) => Task.FromResult(AuthenticateResult.NoResult());
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
        {
            Forbids++;
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }
        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
    }

    private static (DefaultHttpContext Http, RecordingAuthentication Auth) Context()
    {
        var auth = new RecordingAuthentication();
        var services = new ServiceCollection().AddLogging().AddSingleton<IAuthenticationService>(auth).BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        http.Response.Body = new MemoryStream();
        return (http, auth);
    }

    private static async Task<string> BodyOf(HttpContext http)
    {
        http.Response.Body.Position = 0;
        return await new StreamReader(http.Response.Body).ReadToEndAsync();
    }

    // The failure the real policy evaluation produces for an owner token (not a hand-built one).
    private static async Task<PolicyAuthorizationResult> ForbidFor(string policy, ClaimsPrincipal user)
    {
        var result = await Authorization.AuthorizeAsync(user, null, policy);
        Assert.False(result.Succeeded);
        return PolicyAuthorizationResult.Forbid(result.Failure);
    }

    [Theory]
    [InlineData(UserRoles.StayOwner)]
    [InlineData(UserRoles.TaxiDriver)]
    public async Task Seeker_requirement_failure_writes_the_typed_403(string role)
    {
        var (http, auth) = Context();
        var forbid = await ForbidFor(AuthPolicies.SeekerOnly, SeekerRouteTable.User(role));
        Assert.True(SeekerGate.FailedSeekerRequirement(forbid));

        var nextRan = false;
        await new SeekerForbiddenResultHandler().HandleAsync(_ => { nextRan = true; return Task.CompletedTask; }, http,
            new AuthorizationPolicyBuilder().RequireClaim(ClaimTypes.Role, UserRoles.Seeker).Build(), forbid);

        Assert.False(nextRan);
        Assert.Equal(0, auth.Forbids);
        Assert.Equal(403, http.Response.StatusCode);
        using var doc = JsonDocument.Parse(await BodyOf(http));
        Assert.Equal("403", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(403, doc.RootElement.GetProperty("code").GetInt32());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("data").ValueKind);
        var error = doc.RootElement.GetProperty("error");
        Assert.Equal("SeekerOnly", error.GetProperty("type").GetString());
        Assert.Equal("These features are for travellers. Use a seeker account.", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Driver_policy_failure_keeps_the_framework_forbid_without_this_body()
    {
        var (http, auth) = Context();
        var forbid = await ForbidFor("CanListTaxis", SeekerRouteTable.User(UserRoles.Seeker));
        Assert.False(SeekerGate.FailedSeekerRequirement(forbid));

        await new SeekerForbiddenResultHandler().HandleAsync(_ => Task.CompletedTask, http,
            new AuthorizationPolicyBuilder().RequireClaim(ClaimTypes.Role, UserRoles.TaxiDriver).Build(), forbid);

        Assert.Equal(1, auth.Forbids);
        Assert.Equal(403, http.Response.StatusCode);
        Assert.Equal(string.Empty, await BodyOf(http));
    }

    [Fact]
    public async Task Success_runs_the_endpoint()
    {
        var (http, auth) = Context();
        var nextRan = false;
        await new SeekerForbiddenResultHandler().HandleAsync(_ => { nextRan = true; return Task.CompletedTask; }, http,
            new AuthorizationPolicyBuilder().RequireClaim(ClaimTypes.Role, UserRoles.Seeker).Build(), PolicyAuthorizationResult.Success());

        Assert.True(nextRan);
        Assert.Equal(0, auth.Forbids);
    }

    [Fact]
    public void The_api_registers_this_handler()
    {
        var services = new ServiceCollection();
        services.AddJwtAuthentication(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:Key"] = "unit-test-signing-key-unit-test-signing-key" }).Build());
        services.AddLogging();
        Assert.IsType<SeekerForbiddenResultHandler>(services.BuildServiceProvider().GetRequiredService<IAuthorizationMiddlewareResultHandler>());
    }

    [Fact]
    public void Message_is_the_app_line()
    {
        Assert.Equal("SeekerOnly", AuthPolicies.SeekerOnly);
        Assert.Equal("SeekerOnly", AuthPolicies.ErrorType);
        Assert.Equal("These features are for travellers. Use a seeker account.", AuthPolicies.SeekerOnlyMessage);
    }
}
