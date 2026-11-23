using Microsoft.Extensions.Configuration;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using RentNearBy.Api.Filters;
using RentNearBy.Api.Handlers;
using RentNearBy.Api.Handlers.Photos;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Tests.Trips;

namespace RentNearBy.Tests.Users;

// CONTRACT_BACKEND_DP 7.2 (upload handler, limiter, route) and 7.5 (EF model).
public class ProfilePhotoTests
{
    private static string Src(params string[] parts) => File.ReadAllText(Path.Combine([CompletionCounterGuardTests.Root(), .. parts]));

    private static int Status(IResult r) => (r as IStatusCodeHttpResult)?.StatusCode ?? 200;

    private static IFormFile Form(byte[] bytes, long? length = null)
        => new FormFile(new MemoryStream(bytes), 0, length ?? bytes.Length, "photo", "me.jpg");

    private static ClaimsPrincipal Caller(Guid id) => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id.ToString())], "test"));

    private static Task<IResult> Upload(IFormFile? file, ClaimsPrincipal principal, RecordingPhotoService photos)
        => UserPhotoHandlers.UploadPhoto(file, principal, null!, null!, photos, null!, null!, null!);

    [Fact]
    public async Task No_user_in_the_token_is_401_and_nothing_is_saved()
    {
        var photos = new RecordingPhotoService();
        Assert.Equal(401, Status(await Upload(Form(TestImages.SmallJpeg()), new ClaimsPrincipal(), photos)));
        Assert.Empty(photos.Saved);
    }

    public static TheoryData<string> BadFiles => ["null", "empty", "bmp", "gif", "heic", "garbage", "too-big"];

    [Theory]
    [MemberData(nameof(BadFiles))]
    public async Task Refusals_happen_before_any_cloudinary_save(string kind)
    {
        var photos = new RecordingPhotoService();
        var file = kind switch
        {
            "null" => null,
            "empty" => Form(TestImages.Empty()),
            "bmp" => Form(TestImages.Bmp()),
            "gif" => Form(TestImages.Gif()),
            "heic" => Form(TestImages.Heic()),
            "garbage" => Form(TestImages.Garbage()),
            _ => Form(TestImages.SmallJpeg(), PhotoUpload.MaxBytes + 1),
        };
        Assert.Equal(400, Status(await Upload(file, Caller(Guid.NewGuid()), photos)));
        Assert.Empty(photos.ProfileSaves);
        Assert.Empty(photos.Saved);
    }

    // AU7-1: the real pipeline behind PUT /users/me/photo; a decoder crash must reach the user as 400, before Cloudinary or SQL.
    [Theory]
    [InlineData("broken")]
    [InlineData("vp8x")]
    public async Task Damaged_webp_is_400_with_no_save_and_no_sql(string kind)
    {
        var bytes = kind == "broken" ? TestImages.BrokenWebp() : TestImages.EmptyVp8xWebp();
        var photos = new PhotoService(new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cloudinary:CloudName"] = "unit", ["Cloudinary:ApiKey"] = "unit", ["Cloudinary:ApiSecret"] = "unit",
            }).Build());
        var http = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
        var middleware = new RentNearBy.Api.Middleware.ErrorHandlingMiddleware(async ctx =>
        {
            var result = await UserPhotoHandlers.UploadPhoto(Form(bytes), Caller(Guid.NewGuid()), null!, null!, photos, null!, null!, null!);
            await result.ExecuteAsync(ctx);
        }, Microsoft.Extensions.Logging.Abstractions.NullLogger<RentNearBy.Api.Middleware.ErrorHandlingMiddleware>.Instance);

        await middleware.InvokeAsync(http);

        Assert.Equal(400, http.Response.StatusCode);
        http.Response.Body.Position = 0;
        Assert.Contains(InvalidPhotoException.UserMessage, new StreamReader(http.Response.Body).ReadToEnd());
    }

    [Fact]
    public void Limiter_key_is_its_own_bucket_10_an_hour()
    {
        var id = Guid.NewGuid();
        var limit = OwnerRateLimitKeys.ProfilePhoto(id);
        Assert.Equal($"user:photo:{id}", limit.Key);
        Assert.Equal(10, limit.Max);
        Assert.Equal(TimeSpan.FromHours(1), limit.Window);
        Assert.Null(limit.Attempt);
        Assert.NotEqual(OwnerRateLimitKeys.Photo(id).Key, limit.Key);
    }

    [Fact]
    public async Task Eleventh_upload_in_an_hour_is_429_with_retry_after()
    {
        var filter = new UserPhotoLimitFilter(new InMemoryRateLimitService());
        var http = new DefaultHttpContext { User = Caller(Guid.NewGuid()) };
        var calls = 0;
        object? last = null;
        for (var i = 0; i < 11; i++)
            last = await filter.InvokeAsync(new DefaultEndpointFilterInvocationContext(http), _ =>
            {
                calls++;
                return ValueTask.FromResult<object?>(Results.Ok());
            });
        Assert.Equal(10, calls);
        Assert.Equal(429, Status((IResult)last!));
        Assert.True(http.Response.Headers.ContainsKey("Retry-After"));
    }

    [Fact]
    public async Task Anonymous_call_passes_the_filter_to_the_auth_answer()
    {
        var filter = new UserPhotoLimitFilter(new InMemoryRateLimitService());
        var reached = false;
        await filter.InvokeAsync(new DefaultEndpointFilterInvocationContext(new DefaultHttpContext()), _ =>
        {
            reached = true;
            return ValueTask.FromResult<object?>(null);
        });
        Assert.True(reached);
    }

    [Fact]
    public void Route_is_put_only_with_body_cap_and_the_photo_limit()
    {
        var src = Src("RentNearBy.Api", "Endpoints", "UsersEndpoints.cs");
        var line = src.Split('\n').Single(l => l.Contains("UserPhotoHandlers.UploadPhoto"));
        Assert.Contains("group.MapPut(\"/me/photo\", UserPhotoHandlers.UploadPhoto).RequireAuthorization().DisableAntiforgery()", line);
        Assert.Contains(".UserPhotoLimited()", line);
        Assert.Contains(".PhotoBodyLimited();", line);
        Assert.DoesNotContain("OwnerPhotoLimited", line);
        Assert.DoesNotContain("/me/photo\", ", src.Replace(line, string.Empty));
        Assert.DoesNotContain("MapDelete", src);
        Assert.Equal(11 * 1024 * 1024, PhotoBodyLimit.MaxRequestBytes);
    }

    private static readonly IModel Model = BuildModel();

    private static IModel BuildModel()
    {
        using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=localhost;Database=design_only", o => o.UseNetTopologySuite()).Options);
        return db.GetService<IDesignTimeModel>().Model;
    }

    [Theory]
    [InlineData(nameof(Core.Entities.User.ProfilePhotoUrl), 500)]
    [InlineData(nameof(Core.Entities.User.ProfilePhotoPath), 300)]
    [InlineData(nameof(Core.Entities.User.ProfilePhotoUpdatedAt), null)]
    public void Users_columns_are_nullable_capped_and_ignored_by_save_changes(string name, int? maxLength)
    {
        var p = Model.FindEntityType(typeof(Core.Entities.User))!.FindProperty(name)!;
        Assert.True(p.IsNullable);
        Assert.Equal(maxLength, p.GetMaxLength());
        Assert.Equal(PropertySaveBehavior.Ignore, p.GetAfterSaveBehavior());
        Assert.Empty(p.GetContainingIndexes());
    }

    [Fact]
    public void Url_and_path_are_set_or_cleared_together()
    {
        var check = Model.FindEntityType(typeof(Core.Entities.User))!.GetCheckConstraints().Single(c => c.Name == "ck_users_profilephoto_pair");
        Assert.Equal("(\"ProfilePhotoUrl\" IS NULL) = (\"ProfilePhotoPath\" IS NULL)", check.Sql);
    }
}
