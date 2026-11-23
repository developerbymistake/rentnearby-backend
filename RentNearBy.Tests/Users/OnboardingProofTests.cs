using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using RentNearBy.Api.Handlers;
using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.Users;

// CONTRACT_BACKEND_DP decision 14 / 7.7: complete-onboarding needs the signup reservation that verify-otp writes.
public class OnboardingProofTests
{
    private const string Phone = "9876543210";
    private const string Held = "Bakhli#48291736";

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
        MappingsOnce.Ensure();
        var cache = new MemoryCache(new MemoryCacheOptions());
        cache.Set(AppTabCache.Key, new List<AppTabDto>());
        return cache;
    }

    private static int Status(IResult r) => (r as IStatusCodeHttpResult)?.StatusCode ?? 200;

    private static ApiError? Error(IResult r) => (((IValueHttpResult)r).Value as ApiResponse<object>)?.Error;

    private static Task<IResult> Verify(UsernameWorld w)
        => AuthHandlers.PhoneVerifyOtp(new PhoneLoginVerifyOtpRequest { PhoneNumber = Phone, Otp = "1234" },
            new PhoneLoginVerifyOtpRequestValidator(), w.Uow, new Jwt(), new AllowAll(), new OtpOk(),
            new DefaultHttpContext(), Cache(), w.Service("Bakhli#48291736"));

    private static HttpContext From(string ip)
    {
        var h = new DefaultHttpContext();
        h.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(ip);
        return h;
    }

    private static Microsoft.Extensions.Configuration.IConfiguration Config(bool? allowWithoutProof = null)
    {
        var values = new Dictionary<string, string?>();
        if (allowWithoutProof != null) values["Auth:AllowOnboardingWithoutProof"] = allowWithoutProof.Value ? "true" : "false";
        return new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static Task<IResult> Complete(UsernameWorld w, string? username = null, IRateLimitService? limiter = null,
        bool? allowWithoutProof = null, HttpContext? http = null)
        => AuthHandlers.PhoneCompleteOnboarding(new PhoneOnboardingRequest { PhoneNumber = Phone, Name = "Asha", Username = username },
            new PhoneOnboardingRequestValidator(), w.Uow, new Jwt(), Cache(), w.Service("Bakhli#11111111"),
            limiter ?? new AllowAll(), http ?? new DefaultHttpContext(), Config(allowWithoutProof));

    [Fact]
    public async Task A_wrong_username_proof_is_refused_and_no_user_is_made()
    {
        var w = new UsernameWorld();
        await Verify(w);

        var result = await Complete(w, username: "Bakhli#99999999");

        Assert.Equal(400, Status(result));
        Assert.Equal("OtpRequired", Error(result)!.Type);
        Assert.Empty(w.Repo.Added);
    }

    [Fact]
    public async Task The_right_username_proof_makes_the_user()
    {
        var w = new UsernameWorld();
        await Verify(w);

        var result = await Complete(w, username: "Bakhli#48291736", allowWithoutProof: false);

        Assert.Equal(200, Status(result));
        Assert.Single(w.Repo.Added);
    }

    [Fact]
    public async Task Without_a_username_proof_is_refused_once_the_switch_is_off()
    {
        var w = new UsernameWorld();
        await Verify(w);

        var result = await Complete(w, allowWithoutProof: false);

        Assert.Equal(400, Status(result));
        Assert.Equal("OtpRequired", Error(result)!.Type);
        Assert.Empty(w.Repo.Added);
    }

    [Fact]
    public async Task The_sixth_complete_in_an_hour_for_one_phone_is_429()
    {
        var w = new UsernameWorld();
        var limiter = new InMemoryRateLimitService();
        for (var i = 0; i < AuthHandlers.OnboardPhoneMax; i++)
            Assert.Equal("OtpRequired", Error(await Complete(w, limiter: limiter))!.Type);

        var http = new DefaultHttpContext();
        var result = await Complete(w, limiter: limiter, http: http);

        Assert.Equal(429, Status(result));
        Assert.True(int.Parse(http.Response.Headers["Retry-After"]!) >= 1);
        Assert.Empty(w.Repo.Added);
    }

    [Fact]
    public async Task Without_verify_the_answer_is_otp_required_and_no_user_is_made()
    {
        var w = new UsernameWorld();

        var result = await Complete(w);

        Assert.Equal(400, Status(result));
        Assert.Equal("OtpRequired", Error(result)!.Type);
        Assert.Equal("Please verify your phone number again.", Error(result)!.Message);
        Assert.Empty(w.Repo.Added);
        Assert.Equal(0, w.UowState.Saves);
    }

    [Fact]
    public async Task Verify_then_complete_makes_the_user_and_uses_the_reservation_up()
    {
        var w = new UsernameWorld();
        await Verify(w);
        Assert.True(w.Pending.Rows.ContainsKey(Phone));

        var result = await Complete(w, username: Held);

        Assert.Equal(200, Status(result));
        Assert.Single(w.Repo.Added);
        Assert.False(w.Pending.Rows.ContainsKey(Phone));
    }

    [Fact]
    public async Task A_second_complete_never_makes_a_second_user()
    {
        var w = new UsernameWorld();
        await Verify(w);
        Assert.Equal(200, Status(await Complete(w, username: Held)));

        var again = await Complete(w, username: Held);

        Assert.Equal(400, Status(again));
        Assert.Equal("OtpRequired", Error(again)!.Type);
        Assert.Single(w.Repo.Added);
    }

    [Fact]
    public async Task Reservation_check_reads_the_pending_store()
    {
        var w = new UsernameWorld();
        var service = w.Service();
        Assert.False(await service.HasSignupReservationAsync(Phone));
        await service.ReserveForSignupAsync(Phone);
        Assert.True(await service.HasSignupReservationAsync(Phone));
    }
    [Fact]
    public async Task Without_a_username_the_default_configuration_refuses()
    {
        var w = new UsernameWorld();
        await Verify(w);

        var result = await Complete(w);

        Assert.Equal(400, Status(result));
        Assert.Equal("OtpRequired", Error(result)!.Type);
        Assert.Empty(w.Repo.Added);
        Assert.True(w.Pending.Rows.ContainsKey(Phone));
    }

    [Fact]
    public async Task Without_a_username_the_switch_on_still_lets_old_builds_sign_up()
    {
        var w = new UsernameWorld();
        await Verify(w);

        var result = await Complete(w, allowWithoutProof: true);

        Assert.Equal(200, Status(result));
        Assert.Single(w.Repo.Added);
    }

    [Fact]
    public async Task Junk_calls_from_a_shared_address_never_block_a_proven_signup()
    {
        var w = new UsernameWorld();
        var limiter = new InMemoryRateLimitService();
        for (var i = 0; i < AuthHandlers.OnboardIpMax; i++)
            await AuthHandlers.PhoneCompleteOnboarding(new PhoneOnboardingRequest { PhoneNumber = $"98{i:D8}", Name = "Bot" },
                new PhoneOnboardingRequestValidator(), w.Uow, new Jwt(), Cache(), w.Service("Bakhli#11111111"), limiter, From("203.0.113.9"), Config());
        Assert.Equal(429, Status(await Complete(w, limiter: limiter, http: From("203.0.113.9"))));
        await Verify(w);

        var result = await Complete(w, username: Held, limiter: limiter, http: From("203.0.113.9"));

        Assert.Equal(200, Status(result));
        Assert.Single(w.Repo.Added);
    }

    [Fact]
    public async Task Anonymous_calls_never_lock_the_verified_person_out()
    {
        var w = new UsernameWorld();
        var limiter = new InMemoryRateLimitService();
        for (var i = 0; i < AuthHandlers.OnboardPhoneMax; i++) await Complete(w, limiter: limiter);
        await Verify(w);

        Assert.Equal(200, Status(await Complete(w, username: Held, limiter: limiter)));
    }

    [Fact]
    public async Task A_wrong_username_is_charged_and_refused()
    {
        var w = new UsernameWorld();
        var limiter = new InMemoryRateLimitService();
        await Verify(w);
        for (var i = 0; i < AuthHandlers.OnboardPhoneMax; i++)
            Assert.Equal("OtpRequired", Error(await Complete(w, username: $"Bakhli#9999999{i}", limiter: limiter))!.Type);

        var http = new DefaultHttpContext();
        var result = await Complete(w, username: "Bakhli#99999990", limiter: limiter, http: http);

        Assert.Equal(429, Status(result));
        Assert.True(http.Response.Headers.ContainsKey("Retry-After"));
        Assert.Empty(w.Repo.Added);
    }

    [Fact]
    public async Task Classify_tells_bound_unbound_and_none_apart()
    {
        var w = new UsernameWorld();
        var service = w.Service("Bakhli#48291736");
        Assert.Equal(SignupProof.None, await service.ClassifySignupAsync(Phone, Held));
        await service.ReserveForSignupAsync(Phone);
        Assert.Equal(SignupProof.Bound, await service.ClassifySignupAsync(Phone, Held));
        Assert.Equal(SignupProof.Unbound, await service.ClassifySignupAsync(Phone, null));
        Assert.Equal(SignupProof.None, await service.ClassifySignupAsync(Phone, "Bakhli#00000000"));
    }
}
