using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using RentNearBy.Api.Handlers;

namespace RentNearBy.Tests.Account;

// DELETE /account wire shape: 409 AccountHasActiveItems with data.reasons, error.message = first reason (old apps toast it).
public class AccountDeleteResponseTests
{
    private sealed class StubDeletion(AccountDeletionResult result) : IAccountDeletionService
    {
        public int Calls { get; private set; }
        public Task<AccountDeletionResult> DeleteAccountAsync(Guid userId) { Calls++; return Task.FromResult(result); }
    }

    private sealed class Limiter(bool allow) : IRateLimitService
    {
        public List<(string Key, int Max, TimeSpan Window)> Checks { get; } = [];

        public Task<RateLimitResult> CheckAsync(string key, int maxAttempts, TimeSpan window, int permits = 1)
        {
            Checks.Add((key, maxAttempts, window));
            return Task.FromResult(new RateLimitResult(allow, allow ? maxAttempts - 1 : 0, allow ? null : TimeSpan.FromMinutes(3)));
        }

        public Task ReleaseAsync(string key, string token) => Task.CompletedTask;
    }

    private static Task<IResult> Delete(ClaimsPrincipal user, IAccountDeletionService service, IRateLimitService? limiter = null, HttpContext? http = null)
        => AccountHandlers.DeleteAccount(user, service, limiter ?? new Limiter(true), http ?? new DefaultHttpContext());

    private static ClaimsPrincipal User(Guid id) => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id.ToString())], "test"));

    private static async Task<(int Status, JsonElement Body)> RunAsync(IResult result)
    {
        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        http.Response.Body = new MemoryStream();
        await result.ExecuteAsync(http);
        http.Response.Body.Position = 0;
        var text = await new StreamReader(http.Response.Body).ReadToEndAsync();
        return (http.Response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement);
    }

    [Fact]
    public async Task Blocked_result_is_409_with_type_and_reasons()
    {
        var blocks = AccountDeletionRules.Blocks(new AccountActivity(true, false, false, false, false, true));
        var stub = new StubDeletion(new AccountDeletionResult(blocks));

        var (status, body) = await RunAsync(await Delete(User(Guid.NewGuid()), stub));

        Assert.Equal(409, status);
        Assert.Equal("AccountHasActiveItems", body.GetProperty("error").GetProperty("type").GetString());
        Assert.Equal(AccountDeletionReasons.Messages[AccountDeletionReasons.ActiveTaxiBooking],
            body.GetProperty("error").GetProperty("message").GetString());
        var reasons = body.GetProperty("data").GetProperty("reasons").EnumerateArray().ToList();
        Assert.Equal(["ActiveTaxiBooking", "LiveListing"], reasons.Select(r => r.GetProperty("code").GetString()));
        Assert.Equal("Take your listing offline first.", reasons[1].GetProperty("message").GetString());
    }

    [Fact]
    public async Task Deleted_result_is_204()
    {
        var stub = new StubDeletion(AccountDeletionResult.Done);
        var (status, _) = await RunAsync(await Delete(User(Guid.NewGuid()), stub));
        Assert.Equal(204, status);
        Assert.Equal(1, stub.Calls);
    }

    [Fact]
    public async Task No_user_claim_is_401()
    {
        var stub = new StubDeletion(AccountDeletionResult.Done);
        var (status, _) = await RunAsync(await Delete(new ClaimsPrincipal(new ClaimsIdentity()), stub));
        Assert.Equal(401, status);
        Assert.Equal(0, stub.Calls);
    }

    // M-8: DELETE /account takes its own bucket (account:delete:{userId}, 5 per 10 minutes) BEFORE the Google pre-check and the
    // deletion; a refused call is 429 TooManyRequests with Retry-After and touches nothing.
    [Fact]
    public async Task Own_bucket_is_taken_first_and_a_refusal_is_429_with_retry_after()
    {
        var user = Guid.NewGuid();
        var stub = new StubDeletion(AccountDeletionResult.Done);
        var allow = new Limiter(true);
        Assert.Equal(204, (await RunAsync(await Delete(User(user), stub, allow))).Status);
        Assert.Equal(($"account:delete:{user}", 5, TimeSpan.FromMinutes(10)), Assert.Single(allow.Checks));

        var denied = new StubDeletion(AccountDeletionResult.Done);
        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        http.Response.Body = new MemoryStream();
        var result = await Delete(User(user), denied, new Limiter(false), http);
        await result.ExecuteAsync(http);
        Assert.Equal(429, http.Response.StatusCode);
        Assert.Equal("180", http.Response.Headers.RetryAfter.ToString());
        Assert.Equal(0, denied.Calls);
    }
}
