using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using RentNearBy.Api.Handlers;
using RentNearBy.Api.Mappings;
using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.Users;

public class AuthUsernameTests
{
    private const string Phone = "9876543210";

    private sealed class AllowAll : IRateLimitService
    {
        public Task<RateLimitResult> CheckAsync(string key, int maxAttempts, TimeSpan window, int permits = 1)
            => Task.FromResult(new RateLimitResult(true, maxAttempts, null));
        public Task ReleaseAsync(string key, string token) => Task.CompletedTask;
    }

    private sealed class OtpOk : IOtpService
    {
        public Task<bool> SendOtpAsync(string phoneNumber, string keyNamespace = "user") => Task.FromResult(true);
        public Task<bool> VerifyOtpAsync(string phoneNumber, string otp, string keyNamespace = "user") => Task.FromResult(true);
    }

    private sealed class Jwt : IJwtService
    {
        public string GenerateToken(User user, Guid sessionId) => "token";
        public string GenerateAdminToken(Admin admin, Guid sessionId) => "admin";
    }

    private static IMemoryCache Cache()
    {
        DtoMappings.ConfigureMappings();
        var cache = new MemoryCache(new MemoryCacheOptions());
        cache.Set(AppTabCache.Key, new List<AppTabDto>());
        return cache;
    }

    private static PhoneLoginResponse Body(IResult result)
        => ((ApiResponse<PhoneLoginResponse>)((IValueHttpResult)result).Value!).Data!;

    private static Task<IResult> Verify(UsernameWorld w, UsernameService usernames)
        => AuthHandlers.PhoneVerifyOtp(new PhoneLoginVerifyOtpRequest { PhoneNumber = Phone, Otp = "1234" },
            new PhoneLoginVerifyOtpRequestValidator(), w.Uow, new Jwt(), new AllowAll(), new OtpOk(),
            new DefaultHttpContext(), Cache(), usernames);

    [Fact]
    public async Task Otp_verify_of_a_new_phone_returns_the_username_before_the_profile_is_sent()
    {
        var w = new UsernameWorld();

        var body = Body(await Verify(w, w.Service("Bakhli#48291736")));

        Assert.True(body.NeedsOnboarding);
        Assert.Null(body.Token);
        Assert.Equal("Bakhli#48291736", body.Username);
        Assert.Equal("Bakhli#48291736", w.Pending.Rows[Phone].Name);
    }

    [Fact]
    public async Task Complete_onboarding_creates_the_user_with_the_username_shown_at_verify()
    {
        var w = new UsernameWorld();
        var shown = Body(await Verify(w, w.Service("Bakhli#48291736"))).Username;
        User? atSave = null;
        w.UowState.OnSave = _ => atSave ??= w.Repo.Added.SingleOrDefault();

        var body = Body(await AuthHandlers.PhoneCompleteOnboarding(
            new PhoneOnboardingRequest { PhoneNumber = Phone, Name = "Asha" },
            new PhoneOnboardingRequestValidator(), w.Uow, new Jwt(), Cache(), w.Service("Bakhli#11111111"),
            new AllowAll(), new DefaultHttpContext(), new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()));

        var created = Assert.Single(w.Repo.Added);
        Assert.Same(created, atSave);
        Assert.Equal(shown, created.Username);
        Assert.False(body.NeedsOnboarding);
        Assert.Equal(shown, body.Username);
        Assert.Equal(shown, body.User!.Username);
        Assert.Equal("Asha", body.User.Name);
        Assert.False(w.Pending.Rows.ContainsKey(Phone));
    }

    [Fact]
    public async Task Login_of_an_existing_user_without_a_username_backfills_it()
    {
        var w = new UsernameWorld();
        var user = new User { Id = Guid.NewGuid(), PhoneNumber = Phone, Name = "Asha", IsPhoneVerified = true };
        w.Repo.Row = user;
        w.Repo.Stored[user.Id] = null;

        var body = Body(await Verify(w, w.Service("Bakhli#48291736")));

        Assert.False(body.NeedsOnboarding);
        Assert.Equal("Bakhli#48291736", body.User!.Username);
        Assert.Equal("Bakhli#48291736", body.Username);
        Assert.Equal("Bakhli#48291736", w.Repo.Stored[user.Id]);
        Assert.Empty(w.Pending.Rows);
    }
}
