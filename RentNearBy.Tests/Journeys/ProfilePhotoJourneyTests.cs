using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using RentNearBy.Api.Handlers;
using RentNearBy.Api.Handlers.Photos;
using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Tests.Users;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace RentNearBy.Tests.Journeys;

// DP-TEST: the profile picture journey without a database. Journeys that need PostGIS are in
// Integration/Journeys/ProfilePhotoJourneyIntegrationTests (written, NOT RUN here).
public class ProfilePhotoJourneyTests
{
    private const string Phone = "9876543210";

    private static IFormFile Upload(byte[] bytes)
        => new FormFile(new MemoryStream(bytes), 0, bytes.Length, "photo", "me.jpg");

    // ---------- what the user picks: one decision matrix over the shared image set ----------

    public static TheoryData<string> Accepted => new() { "PortraitExif6", "TransparentPng", "AnimatedWebp", "Wide", "Tall", "Small", "Png", "Webp" };
    public static TheoryData<string> RefusedByCheck => new() { "Empty", "Bmp", "Gif", "Heic", "Garbage" };

    private static byte[] Fixture(string name) => (byte[])typeof(TestImages).GetMethod(name)!.Invoke(null, null)!;

    [Theory]
    [MemberData(nameof(Accepted))]
    public async Task Accepted_picture_passes_the_check_and_is_stored_as_a_small_square_jpeg(string name)
    {
        var bytes = Fixture(name);
        Assert.Null(PhotoUpload.Check(Upload(bytes)));

        using var output = await PhotoService.ProcessImageAsync(new MemoryStream(bytes), 640, square: true);
        using var image = await Image.LoadAsync<Rgba32>(new MemoryStream(output.ToArray()));

        Assert.Equal(1, image.Frames.Count);
        Assert.Equal(image.Width, image.Height);
        Assert.InRange(image.Width, 1, 640);
        Assert.Null(image.Metadata.ExifProfile);
        Assert.True(output.Length <= 300 * 1024,$"{name} is {output.Length} bytes");
    }

    [Fact]
    public async Task Large_pictures_are_cropped_to_640_and_small_ones_are_not_enlarged()
    {
        foreach (var (name, side) in new[] { ("Wide", 640), ("Tall", 640), ("Small", 200), ("PortraitExif6", 300) })
        {
            using var output = await PhotoService.ProcessImageAsync(new MemoryStream(Fixture(name)), 640, square: true);
            using var image = await Image.LoadAsync(new MemoryStream(output.ToArray()));
            Assert.Equal((side, side), (image.Width, image.Height));
        }
    }

    [Theory]
    [MemberData(nameof(RefusedByCheck))]
    public void Refused_picture_never_reaches_the_pipeline_or_cloudinary(string name)
    {
        var refusal = PhotoUpload.Check(Upload(Fixture(name)));

        Assert.NotNull(refusal);
        Assert.Equal(400, ((IStatusCodeHttpResult)refusal).StatusCode);
    }

    [Fact]
    public async Task Huge_header_passes_the_byte_check_and_is_stopped_by_the_40_MP_cap()
    {
        Assert.Null(PhotoUpload.Check(Upload(TestImages.HugeHeaderPng())));
        await Assert.ThrowsAsync<InvalidPhotoException>(
            () => PhotoService.ProcessImageAsync(new MemoryStream(TestImages.HugeHeaderPng()), 640, square: true));
    }

    // ---------- onboarding proof (decision 8): complete-onboarding needs the reservation verify-otp wrote ----------

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

    private static Task<IResult> Verify(UsernameWorld w)
        => AuthHandlers.PhoneVerifyOtp(new PhoneLoginVerifyOtpRequest { PhoneNumber = Phone, Otp = "1234" },
            new PhoneLoginVerifyOtpRequestValidator(), w.Uow, new Jwt(), new AllowAll(), new OtpOk(),
            new DefaultHttpContext(), Cache(), w.Service());

    private static string HeldUsername(IResult verified)
        => ((ApiResponse<PhoneLoginResponse>)((IValueHttpResult)verified).Value!).Data!.Username!;

    private static Task<IResult> Complete(UsernameWorld w, string? username = null)
        => AuthHandlers.PhoneCompleteOnboarding(new PhoneOnboardingRequest { PhoneNumber = Phone, Name = "Asha", Username = username },
            new PhoneOnboardingRequestValidator(), w.Uow, new Jwt(), Cache(), w.Service(),
            new AllowAll(), new DefaultHttpContext(), new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

    [Fact]
    public async Task Onboarding_cannot_be_completed_without_the_otp_proof()
    {
        var w = new UsernameWorld();

        var result = await Complete(w);

        Assert.Equal(400, ((IStatusCodeHttpResult)result).StatusCode);
        var body = (ApiResponse<object>)((IValueHttpResult)result).Value!;
        Assert.Equal("OtpRequired", body.Error!.Type);
        Assert.Empty(w.Repo.Added);
        Assert.Equal(0, w.UowState.Saves);
    }

    [Fact]
    public async Task Onboarding_after_verify_creates_one_user_and_the_proof_is_used_up()
    {
        var w = new UsernameWorld();
        var held = HeldUsername(await Verify(w));

        var first = await Complete(w, held);
        var replay = await Complete(w, held);

        Assert.Equal(200, ((IStatusCodeHttpResult)first).StatusCode);
        Assert.Single(w.Repo.Added);
        Assert.Equal(400, ((IStatusCodeHttpResult)replay).StatusCode);
        Assert.Single(w.Repo.Added);
        Assert.False(w.Pending.Rows.ContainsKey(Phone));
    }

    [Fact]
    public async Task A_new_user_has_no_picture_yet_so_the_app_gate_asks_for_one()
    {
        var w = new UsernameWorld();
        var held = HeldUsername(await Verify(w));

        var result = await Complete(w, held);

        Assert.Equal("token", ((ApiResponse<PhoneLoginResponse>)((IValueHttpResult)result).Value!).Data!.Token);
        var user = ((ApiResponse<PhoneLoginResponse>)((IValueHttpResult)result).Value!).Data!.User!;
        Assert.Null(user.ProfilePhotoUrl);
        Assert.False(user.ProfilePhotoRemoved);
    }
}
