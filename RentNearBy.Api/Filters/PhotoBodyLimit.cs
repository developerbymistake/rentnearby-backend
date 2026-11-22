using Microsoft.AspNetCore.Mvc;

namespace RentNearBy.Api.Filters;

// The whole multipart body is capped before binding buffers it (10 MB photo plus form overhead).
public static class PhotoBodyLimit
{
    public const long MaxRequestBytes = 11 * 1024 * 1024;

    public static RouteHandlerBuilder PhotoBodyLimited(this RouteHandlerBuilder builder)
        => builder.WithMetadata(new RequestSizeLimitAttribute(MaxRequestBytes));
}
