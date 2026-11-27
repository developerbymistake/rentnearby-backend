using RentNearBy.Core.Models.Owner.Quote;

namespace RentNearBy.Infrastructure.Services.Owner.Quote;

// Stay quotation logo and signature (ARCHITECTURE 6): the profile-photo pipeline with a quote box and byte cap, stored as the
// Cloudinary "authenticated" type under bakhli/user_{id}/quote/{kind}/. No public URL ever leaves the server.
public interface IQuoteImageStore
{
    // Returns the Cloudinary public id. InvalidPhotoException = not an image or still too large; PhotoBusyException = gate full.
    Task<string> SaveQuoteImageAsync(Stream input, Guid userId, QuoteImageKind kind);

    // Re-encoded JPEG bytes of the caller's own stored image; null when Cloudinary no longer has it.
    Task<byte[]?> ReadQuoteImageAsync(string publicId, CancellationToken ct);

    Task DeleteQuoteImageAsync(string publicId);

    // Every quote image of the user (role change away from Stay Owner).
    Task DeleteAllQuoteImagesAsync(Guid userId);
}

public static class QuoteImagePaths
{
    public const string DeliveryType = "authenticated";

    public static string Folder(Guid userId) => $"bakhli/user_{userId}/quote/";

    public static string NewPublicId(Guid userId, QuoteImageKind kind)
        => $"{Folder(userId)}{QuoteImageKinds.Wire(kind)}/{Guid.NewGuid()}";

    // A stored path must belong to the user and the kind; anything else is never read or deleted on his behalf.
    public static bool Belongs(string? path, Guid userId, QuoteImageKind kind)
        => path != null && path.StartsWith($"{Folder(userId)}{QuoteImageKinds.Wire(kind)}/", StringComparison.Ordinal);
}
