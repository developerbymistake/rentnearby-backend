using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Models.Owner.Quote;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Support.Owner;

namespace RentNearBy.Tests.Stays.Quotation;

// Q6 / S5 on a real PostgreSQL with a recording store: replace swaps the path with one statement and deletes the old file after it,
// AssetsVersion grows on every change, a failed statement deletes the new file, remove is idempotent, the proxy returns only the
// caller's own image, and a path outside his folder is never read.
public class QuoteImageSwapTests(TestDatabase db) : IntegrationTestBase(db)
{
    private async Task<Guid> Owner()
    {
        await using var c = CreateContext();
        return (await OwnerSeed.StayOwnerAsync(c)).UserId;
    }

    [IntegrationFact]
    public async Task Replace_deletes_the_old_file_and_bumps_the_assets_version()
    {
        var user = await Owner();
        var store = new RecordingQuoteImageStore();
        await using (var c = CreateContext())
        {
            var first = await QuoteKit.Images(c, store).PutAsync(user, QuoteImageKind.Logo, new MemoryStream(TestImages.SmallJpeg()), default);
            Assert.Equal(1, first.Value!.AssetsVersion);
        }
        var firstPath = store.Files.Keys.Single();
        await using (var c = CreateContext())
        {
            var second = await QuoteKit.Images(c, store).PutAsync(user, QuoteImageKind.Logo, new MemoryStream(TestImages.SmallJpeg()), default);
            Assert.Equal(2, second.Value!.AssetsVersion);
        }
        Assert.Equal(new[] { firstPath }, store.Deleted);
        await using var check = CreateContext();
        var row = await check.StayQuoteProfiles.SingleAsync(p => p.UserId == user);
        Assert.NotEqual(firstPath, row.LogoPath);
        Assert.StartsWith($"bakhli/user_{user}/quote/logo/", row.LogoPath);
        Assert.Null(row.SignaturePath);
    }

    [IntegrationFact]
    public async Task Remove_clears_the_path_once_and_is_idempotent()
    {
        var user = await Owner();
        var store = new RecordingQuoteImageStore();
        await using (var c = CreateContext())
            await QuoteKit.Images(c, store).PutAsync(user, QuoteImageKind.Signature, new MemoryStream(TestImages.SmallJpeg()), default);
        await using (var c = CreateContext())
            Assert.Equal(2, (await QuoteKit.Images(c, store).DeleteAsync(user, QuoteImageKind.Signature, default)).Value!.AssetsVersion);
        await using (var c = CreateContext())
            Assert.Equal(2, (await QuoteKit.Images(c, store).DeleteAsync(user, QuoteImageKind.Signature, default)).Value!.AssetsVersion);
        Assert.Single(store.Deleted);
    }

    [IntegrationFact]
    public async Task Proxy_returns_only_the_callers_own_image()
    {
        var a = await Owner();
        var b = await Owner();
        var store = new RecordingQuoteImageStore();
        await using (var c = CreateContext())
            await QuoteKit.Images(c, store).PutAsync(a, QuoteImageKind.Logo, new MemoryStream(TestImages.SmallJpeg()), default);
        await using (var c = CreateContext())
        {
            var images = QuoteKit.Images(c, store);
            Assert.True((await images.ReadAsync(a, QuoteImageKind.Logo, default)).Ok);
            Assert.Equal(QuoteErrors.ImageMissing, (await images.ReadAsync(a, QuoteImageKind.Signature, default)).ErrorType);
            Assert.Equal(QuoteErrors.ImageMissing, (await images.ReadAsync(b, QuoteImageKind.Logo, default)).ErrorType);
        }
    }

    [IntegrationFact]
    public async Task A_stored_path_outside_the_owner_folder_is_never_read()
    {
        var user = await Owner();
        var store = new RecordingQuoteImageStore();
        var foreign = $"bakhli/user_{Guid.NewGuid()}/quote/logo/x";
        store.Files[foreign] = [0xFF, 0xD8, 0xFF];
        await using (var c = CreateContext())
        {
            c.StayQuoteProfiles.Add(new StayQuoteProfile { UserId = user, LogoPath = foreign, AssetsVersion = 1, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            await c.SaveChangesAsync();
        }
        await using var check = CreateContext();
        Assert.Equal(QuoteErrors.ImageMissing, (await QuoteKit.Images(check, store).ReadAsync(user, QuoteImageKind.Logo, default)).ErrorType);
    }

    [IntegrationFact]
    public async Task An_unknown_user_gets_the_new_file_deleted()
    {
        var store = new RecordingQuoteImageStore();
        await using var c = CreateContext();
        var r = await QuoteKit.Images(c, store).PutAsync(Guid.NewGuid(), QuoteImageKind.Logo, new MemoryStream(TestImages.SmallJpeg()), default);
        Assert.False(r.Ok);
        Assert.Single(store.Deleted);
        Assert.Empty(store.Files);
    }

    [IntegrationFact]
    public async Task Pipeline_refusal_writes_nothing()
    {
        var user = await Owner();
        var store = new RecordingQuoteImageStore { Process = (_, _) => throw new InvalidPhotoException() };
        await using var c = CreateContext();
        await Assert.ThrowsAsync<InvalidPhotoException>(() =>
            QuoteKit.Images(c, store).PutAsync(user, QuoteImageKind.Logo, new MemoryStream(TestImages.Gif()), default));
        Assert.False(await c.StayQuoteProfiles.AnyAsync(p => p.UserId == user));
    }
}
