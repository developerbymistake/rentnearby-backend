using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using RentNearBy.Api.Handlers;
using RentNearBy.Api.Handlers.Photos;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Tests.Trips;

namespace RentNearBy.Tests.Regression;

// COMPRESSION_AUDIT 3.2 / CONTRACT_BACKEND_DP 7.10 and 7.11: every upload goes through PhotoUpload.Check, the body cap and
// the one pipeline. A seventh upload route fails here until it is listed on purpose.
public class PhotoUploadGuardTests
{
    private static string Root => CompletionCounterGuardTests.Root();
    private static string Src(params string[] parts) => File.ReadAllText(Path.Combine([Root, .. parts]));

    // Handler (class.method) -> endpoint file that maps it.
    private static readonly Dictionary<string, string> UploadRoutes = new()
    {
        ["StayHandlers.UploadPhoto"] = "StayEndpoints.cs",
        ["StayHandlers.ReplacePhoto"] = "StayEndpoints.cs",
        ["TaxiHandlers.UploadPlatePhoto"] = "TaxiEndpoints.cs",
        ["TaxiHandlers.UploadDisplayPhoto"] = "TaxiEndpoints.cs",
        ["BannerHandlers.AdminCreateBanner"] = "BannerEndpoints.cs",
        ["UserPhotoHandlers.UploadPhoto"] = "UsersEndpoints.cs",
    };

    private static string Method(string src, string name)
    {
        var start = src.IndexOf($"Task<IResult> {name}(", StringComparison.Ordinal);
        Assert.True(start >= 0, name);
        var next = src.IndexOf("\n    public ", start, StringComparison.Ordinal);
        var nextInternal = src.IndexOf("\n    internal ", start, StringComparison.Ordinal);
        var nextPrivate = src.IndexOf("\n    private ", start, StringComparison.Ordinal);
        var end = new[] { next, nextInternal, nextPrivate }.Where(i => i > start).DefaultIfEmpty(src.Length).Min();
        return src[start..end];
    }

    private static bool TakesFile(ParameterInfo p)
    {
        var t = Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType;
        return t == typeof(IFormFile) || typeof(IFormFileCollection).IsAssignableFrom(t)
               || (t.IsGenericType && t.GetGenericArguments().Any(a => a == typeof(IFormFile)));
    }

    private static IEnumerable<string> SourceFiles(string under)
        => Directory.EnumerateFiles(under, "*.cs", SearchOption.AllDirectories)
            .Where(f => !Regex.IsMatch(f, @"[\\/](bin|obj)[\\/]"));

    private static string HandlerSource(Type type)
        => Directory.EnumerateFiles(Path.Combine(Root, "RentNearBy.Api", "Handlers"), "*.cs", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .Single(s => s.Contains($"static class {type.Name}", StringComparison.Ordinal));

    [Fact]
    public void Every_form_file_handler_is_listed_and_checks_before_it_saves()
    {
        var handlers = typeof(StayHandlers).Assembly.GetTypes()
            .Where(t => t != typeof(PhotoUpload))
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(m => m.GetParameters().Any(TakesFile))
                .Select(m => (Type: t, Method: m)))
            .ToList();
        Assert.Equal(UploadRoutes.Keys.Order(), handlers.Select(h => $"{h.Type.Name}.{h.Method.Name}").Order());

        foreach (var (type, method) in handlers)
        {
            var body = Method(HandlerSource(type), method.Name);
            var check = body.IndexOf("PhotoUpload.Check(", StringComparison.Ordinal);
            var save = Regex.Match(body, @"Save(Photo|Banner|ProfilePhoto)Async\(").Index;
            Assert.True(check > 0, $"{type.Name}.{method.Name} has no PhotoUpload.Check");
            Assert.True(save > check, $"{type.Name}.{method.Name} saves before the check");
        }
    }

    [Fact]
    public void Every_upload_route_caps_the_body()
    {
        foreach (var (handler, file) in UploadRoutes)
        {
            var line = Src("RentNearBy.Api", "Endpoints", file).Split('\n').Single(l => l.Contains(handler + ")"));
            Assert.Contains(".DisableAntiforgery()", line);
            Assert.Contains(".PhotoBodyLimited();", line);
        }
    }

    [Fact]
    public void Cloudinary_upload_only_in_photo_service_and_always_after_the_pipeline()
    {
        var files = Directory.EnumerateFiles(Root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains("RentNearBy.Tests"))
            .Where(f => File.ReadAllText(f).Contains("_cloudinary.UploadAsync", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();
        Assert.Equal(["PhotoService.cs"], files);

        var src = Src("RentNearBy.Infrastructure", "Services", "PhotoService.cs");
        var uploads = Regex.Matches(src, @"_cloudinary\.UploadAsync").Select(m => m.Index).ToList();
        Assert.Equal(3, uploads.Count);
        foreach (var at in uploads)
        {
            var methodStart = src.LastIndexOf("public async Task<(string url, string filePath)>", at, StringComparison.Ordinal);
            var process = src.IndexOf("ProcessImageAsync(", methodStart, StringComparison.Ordinal);
            Assert.True(process > methodStart && process < at);
        }
    }

    [Fact]
    public void Pipeline_orients_takes_the_first_frame_and_flattens()
    {
        var src = Src("RentNearBy.Infrastructure", "Services", "PhotoService.cs");
        var body = src[src.IndexOf("ProcessImageAsync(Stream", StringComparison.Ordinal)..];
        Assert.Contains("AutoOrient", body);
        Assert.Contains("MaxFrames = 1", body);
        Assert.Contains("BackgroundColor", body);
    }

    [Fact]
    public void Pipeline_caps_pixels_before_the_decode()
    {
        var src = Src("RentNearBy.Infrastructure", "Services", "PhotoService.cs");
        var body = src[src.IndexOf("ProcessImageAsync(Stream", StringComparison.Ordinal)..];
        var cap = body.IndexOf("MaxPixels", StringComparison.Ordinal);
        Assert.True(cap >= 0 && cap < body.IndexOf("Image.LoadAsync", StringComparison.Ordinal));
    }

    // REVIEW_SEC (DP) S6: a route that reads the form by hand would bypass the file-parameter scan above.
    [Fact]
    public void No_handler_reads_form_files_by_hand()
    {
        var offenders = SourceFiles(Path.Combine(Root, "RentNearBy.Api"))
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"\.Form\.Files|ReadFormAsync\(|IFormFileCollection|IFormCollection"))
            .Select(Path.GetFileName)
            .ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void Cloudinary_client_is_created_only_in_photo_service()
    {
        var files = SourceFiles(Root)
            .Where(f => !f.Contains("RentNearBy.Tests", StringComparison.Ordinal))
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"new Cloudinary\(|_cloudinary\.Upload(Large)?Async\("))
            .Select(Path.GetFileName)
            .ToList();
        Assert.Equal(["PhotoService.cs"], files);
    }

    [Fact]
    public void No_private_magic_byte_copy_is_left()
    {
        foreach (var f in Directory.EnumerateFiles(Path.Combine(Root, "RentNearBy.Api"), "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
            Assert.DoesNotContain("IsValidImageMagicBytes", File.ReadAllText(f));
    }

    // --- Banner (G2): empty / wrong type refused before the upload; a failed save deletes the new file. ---

    public class BannerRepo : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? m, object?[]? a) => m?.Name switch
        {
            nameof(IDistrictBannerRepository.GetByDistrictIdAsync) => Task.FromResult<DistrictBanner?>(null),
            nameof(IRepository<DistrictBanner>.AddAsync) => Task.CompletedTask,
            _ => throw new NotSupportedException(m?.Name),
        };
    }

    public class BannerUow : DispatchProxy
    {
        public IDistrictBannerRepository? Banners;
        public bool FailSave;

        protected override object? Invoke(MethodInfo? m, object?[]? a) => m?.Name switch
        {
            "get_DistrictBanners" => Banners,
            nameof(IUnitOfWork.SaveChangesAsync) => FailSave
                ? Task.FromException<int>(new InvalidOperationException("db down"))
                : Task.FromResult(1),
            _ => throw new NotSupportedException(m?.Name),
        };
    }

    private static IUnitOfWork Uow(bool failSave)
    {
        var uow = DispatchProxy.Create<IUnitOfWork, BannerUow>();
        var state = (BannerUow)(object)uow;
        state.Banners = DispatchProxy.Create<IDistrictBannerRepository, BannerRepo>();
        state.FailSave = failSave;
        return uow;
    }

    private static IFormFile Form(byte[] bytes) => new FormFile(new MemoryStream(bytes), 0, bytes.Length, "image", "b.jpg");

    private static int Status(IResult r) => (r as IStatusCodeHttpResult)?.StatusCode ?? 200;

    private static string? Message(IResult r) => (((IValueHttpResult)r).Value as ApiResponse<object>)?.Error?.Message;

    [Fact]
    public async Task Banner_empty_or_bmp_is_400_without_an_upload()
    {
        foreach (var file in new IFormFile?[] { null, Form(TestImages.Empty()), Form(TestImages.Bmp()) })
        {
            var photos = new RecordingPhotoService();
            var result = await BannerHandlers.AdminCreateBanner(Guid.NewGuid(), null, null, file, Uow(false), photos, null!,
                new ServiceCollection().BuildServiceProvider());
            Assert.Equal(400, Status(result));
            Assert.Equal(PhotoUpload.NotImage, Message(result));
            Assert.Empty(photos.Saved);
        }
    }

    [Fact]
    public async Task Banner_db_failure_after_the_upload_deletes_the_new_file()
    {
        var photos = new RecordingPhotoService();
        await Assert.ThrowsAsync<InvalidOperationException>(() => BannerHandlers.AdminCreateBanner(
            Guid.NewGuid(), null, null, Form(TestImages.SmallJpeg()), Uow(true), photos, null!,
            new ServiceCollection().BuildServiceProvider()));
        var saved = Assert.Single(photos.Saved);
        Assert.Equal(saved, Assert.Single(photos.Deleted));
    }

    [Fact]
    public void Banner_keeps_its_own_too_big_text()
        => Assert.Contains("PhotoUpload.Check(image, PhotoUpload.NotImage, \"Image size must not exceed 10MB\")",
            Src("RentNearBy.Api", "Handlers", "BannerHandlers.cs"));
}
