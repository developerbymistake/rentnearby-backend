using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.Extensions.Logging;
using RentNearBy.Api.Filters;
using RentNearBy.Api.Handlers;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Tests.Trips;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace RentNearBy.Tests.Regression;

// REVIEW_SEC (card photos) S1, S2, S3, S5, S6, S7, S8.
public class CardPhotoSecurityGuardTests
{
    private static string Src(params string[] parts) => File.ReadAllText(Path.Combine([CompletionCounterGuardTests.Root(), .. parts]));

    private static string Method(string src, string signature)
    {
        var start = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        var next = src.IndexOf("\n    public ", start + signature.Length, StringComparison.Ordinal);
        return next < 0 ? src[start..] : src[start..next];
    }

    private static string Taxi => Src("RentNearBy.Api", "Handlers", "TaxiHandlers.cs");

    [Fact]
    public void S1_plate_handler_refuses_under_review_before_upload()
    {
        var body = Method(Taxi, "public static async Task<IResult> UploadPlatePhoto(");
        var review = body.IndexOf("taxi.Status == TaxiStatuses.PendingReview) return LockedResponse()", StringComparison.Ordinal);
        Assert.True(review >= 0);
        Assert.True(review < body.IndexOf("OpenReadStream", StringComparison.Ordinal));
        Assert.True(review < body.IndexOf("SavePhotoAsync", StringComparison.Ordinal));
    }

    [Fact]
    public void S2_plate_number_cannot_change_once_the_proof_is_locked()
    {
        var body = Method(Taxi, "public static async Task<IResult> UpsertBasic(");
        var guard = body.IndexOf("if (!isNew && plateChanged && TaxiPhotoRules.ProofLocked(taxi))", StringComparison.Ordinal);
        Assert.True(guard > body.IndexOf("var plateChanged = taxi.NumberPlate != plate;", StringComparison.Ordinal));
        Assert.True(guard < body.IndexOf("taxi.NumberPlate = plate;", StringComparison.Ordinal));
        Assert.Contains("TaxiErrorTypes.TaxiProofLocked", body[guard..(guard + 300)]);
    }

    [Fact]
    public void S3_cloudinary_deletes_invalidate_the_cdn()
    {
        var src = Src("RentNearBy.Infrastructure", "Services", "PhotoService.cs");
        // One single-file delete, one prefix delete helper (both prefix routes use it) and the stay quotation image delete
        // (authenticated type); each invalidates.
        var deletes = Regex.Matches(src, @"_cloudinary\.(DestroyAsync|DeleteResourcesAsync)\(").Count;
        Assert.Equal(3, deletes);
        Assert.Equal(deletes, Regex.Matches(src, @"Invalidate\s*=\s*true").Count);
        Assert.DoesNotContain("DeleteResourcesByPrefixAsync(", src);
        Assert.Contains("new DeletionParams(filePath) { Invalidate = true }", src);
    }

    [Fact]
    public void S5_replace_sql_carries_owner_and_review_lock()
    {
        var sql = StayHandlers.ReplacePhotoSql;
        Assert.Contains("s.\"UserId\" = @user", sql);
        Assert.Contains("s.\"Status\" <> @review", sql);
        Assert.Contains("NOT s.\"IsDeleted\"", sql);
        Assert.Contains("FOR UPDATE OF p", sql);
    }

    [Fact]
    public void S6_swap_paths_log_orphans_and_never_swallow_silently()
    {
        var stay = Method(Src("RentNearBy.Api", "Handlers", "StayHandlers.cs"), "public static async Task<IResult> ReplacePhoto(");
        var saved = stay.IndexOf("SavePhotoAsync", StringComparison.Ordinal);
        var tryAt = stay.IndexOf("try", saved, StringComparison.Ordinal);
        Assert.True(tryAt > 0 && tryAt < stay.IndexOf("BeginTransactionAsync", StringComparison.Ordinal));
        var catchAt = Regex.Match(stay, @"catch\r?\n").Index;
        Assert.True(catchAt > 0);
        var tail = stay[catchAt..];
        Assert.True(tail.IndexOf("TryDeleteAsync(photoService, filePath", StringComparison.Ordinal)
            < tail.IndexOf("RollbackTransactionAsync", StringComparison.Ordinal));
        foreach (var body in new[]
                 {
                     stay, Method(Taxi, "public static async Task<IResult> UploadPlatePhoto("),
                     Method(Taxi, "public static async Task<IResult> UploadDisplayPhoto("),
                 })
        {
            Assert.DoesNotContain("DeletePhotoAsync(", body);
            Assert.Contains("PhotoCleanup.TryDeleteAsync(", body);
        }
    }

    [Fact]
    public async Task S6_failed_delete_is_logged_by_path()
    {
        var logger = new ListLogger();
        await PhotoCleanup.TryDeleteAsync(new ThrowingDelete(), "bakhli/user_1/listing_2/x", logger);
        Assert.Equal("photo.orphan bakhli/user_1/listing_2/x", Assert.Single(logger.Lines));
        await PhotoCleanup.TryDeleteAsync(new ThrowingDelete(), null, logger);
        Assert.Single(logger.Lines);
    }

    [Fact]
    public async Task S7_broken_body_with_jpeg_magic_is_invalid_photo()
    {
        byte[] bytes = [0xFF, 0xD8, 0xFF, 0xE0, .. new byte[64]];
        await Assert.ThrowsAsync<InvalidPhotoException>(() => PhotoService.ProcessImageAsync(new MemoryStream(bytes)));
    }

    [Fact]
    public async Task S7_valid_small_image_still_processes()
    {
        using var image = new Image<Rgba32>(8, 8);
        var input = new MemoryStream();
        await image.SaveAsPngAsync(input);
        input.Position = 0;
        using var output = await PhotoService.ProcessImageAsync(input);
        Assert.True(output.Length > 0);
    }

    [Fact]
    public void S7_photo_routes_cap_the_request_body()
    {
        var taxi = Src("RentNearBy.Api", "Endpoints", "TaxiEndpoints.cs");
        var stay = Src("RentNearBy.Api", "Endpoints", "StayEndpoints.cs");
        foreach (var route in new[] { "TaxiHandlers.UploadPlatePhoto)", "TaxiHandlers.UploadDisplayPhoto)" })
            Assert.Contains(".PhotoBodyLimited();", taxi.Split('\n').Single(l => l.Contains(route)));
        foreach (var route in new[] { "StayHandlers.UploadPhoto)", "StayHandlers.ReplacePhoto)" })
            Assert.Contains(".PhotoBodyLimited();", stay.Split('\n').Single(l => l.Contains(route)));
        Assert.Equal(11 * 1024 * 1024, PhotoBodyLimit.MaxRequestBytes);
        IRequestSizeLimitMetadata meta = new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(PhotoBodyLimit.MaxRequestBytes);
        Assert.Equal(PhotoBodyLimit.MaxRequestBytes, meta.MaxRequestBodySize);
    }

    [Fact]
    public void S7_invalid_photo_is_answered_400()
        => Assert.Contains("InvalidPhotoException => (HttpStatusCode.BadRequest",
            Src("RentNearBy.Api", "Middleware", "ErrorHandlingMiddleware.cs"));

    [Fact]
    public void S8_admin_can_remove_only_the_display_photo()
    {
        Assert.Contains(
            "group.MapDelete(\"/{id:guid}/display-photo\", AdminTaxiHandlers.RemoveDisplayPhoto).RequireAuthorization(\"AdminOnly\");",
            Src("RentNearBy.Api", "Endpoints", "AdminTaxiEndpoints.cs"));
        var sql = AdminTaxiHandlers.ClearDisplayPhotoSql;
        Assert.Contains("SET \"DisplayPhotoUrl\" = NULL, \"DisplayPhotoPath\" = NULL", sql);
        Assert.DoesNotContain("Plate", sql);
        Assert.DoesNotContain("\"Status\" =", sql);
        var body = Method(Src("RentNearBy.Api", "Handlers", "AdminTaxiHandlers.cs"), "public static async Task<IResult> RemoveDisplayPhoto(");
        Assert.True(body.IndexOf("CommitTransactionAsync", StringComparison.Ordinal)
            < body.IndexOf("TryDeleteAsync(photoService, cleared.OldPath", StringComparison.Ordinal));
        Assert.Equal("driver_photo_removed", NotificationTypes.ToWireValue(NotificationTypes.DriverPhotoRemoved));
    }

    private sealed class ThrowingDelete : IPhotoService
    {
        public Task<(string url, string filePath)> SavePhotoAsync(Stream photoStream, string fileName, Guid userId, Guid listingId)
            => throw new NotSupportedException();
        public Task<(string url, string filePath)> SaveBannerAsync(Stream photoStream, string fileName, Guid districtId)
            => throw new NotSupportedException();
        public Task<(string url, string filePath)> SaveProfilePhotoAsync(Stream photoStream, string fileName, Guid userId)
            => throw new NotSupportedException();
        public Task DeletePhotoAsync(string filePath) => throw new HttpRequestException("cloudinary down");
        public Task DeleteRoomPhotosAsync(Guid userId, Guid listingId) => Task.CompletedTask;
        public Task DeleteAllUserPhotosAsync(Guid userId) => Task.CompletedTask;
        public Task<bool> PingAsync() => Task.FromResult(true);
    }

    private sealed class ListLogger : ILogger
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, exception));
    }
}
