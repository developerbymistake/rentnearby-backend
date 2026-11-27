using System.Security.Claims;
using FluentValidation;
using RentNearBy.Api.Handlers.Photos;
using RentNearBy.Core.DTOs.Owner.Quote;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Core.Models.Owner.Quote;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Infrastructure.Services.Owner.Quote;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers.Owner;

// /api/v1/stays/me/quotation (CanListStays). Thin: caller from the token, read bucket, validation, service, typed answer.
// No route takes an id; no handler logs a request DTO; every validator message is static.
public static class StayQuotationHandlers
{
    public static async Task<IResult> GetHub(
        ClaimsPrincipal principal, StayQuoteProfileService service, IRateLimitService limiter, HttpContext http, CancellationToken ct)
    {
        if (!OwnerHttp.TryOwner(principal, out var me)) return UnauthorizedResponse();
        if (await OwnerHttp.ReadLimitAsync(limiter, http, OwnerRateLimitKeys.Read(me)) is { } denied) return denied;
        NoStore(http);
        var ifNoneMatch = http.Request.Headers.IfNoneMatch.ToString();
        var result = await service.GetHubAsync(me, string.IsNullOrEmpty(ifNoneMatch) ? null : ifNoneMatch, ct);
        if (!result.Ok) return QuoteHttp.Refuse(result);
        http.Response.Headers.ETag = result.Value!.ETag;
        return result.Value.NotModified ? Results.StatusCode(StatusCodes.Status304NotModified) : OkResponse(result.Value.Hub);
    }

    public static async Task<IResult> GetBank(
        ClaimsPrincipal principal, StayQuoteProfileService service, IRateLimitService limiter, HttpContext http, CancellationToken ct)
    {
        if (!OwnerHttp.TryOwner(principal, out var me)) return UnauthorizedResponse();
        if (await OwnerHttp.ReadLimitAsync(limiter, http, OwnerRateLimitKeys.Read(me)) is { } denied) return denied;
        NoStore(http);
        var result = await service.GetBankAsync(me, ct);
        return result.Ok ? OkResponse(result.Value) : QuoteHttp.Refuse(result);
    }

    public static async Task<IResult> PutProfile(
        QuoteProfileRequest request, IValidator<QuoteProfileRequest> validator, ClaimsPrincipal principal,
        StayQuoteProfileService service, HttpContext http, CancellationToken ct)
    {
        if (!OwnerHttp.TryOwner(principal, out var me)) return UnauthorizedResponse();
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid) return QuoteHttp.Invalid(validation);
        NoStore(http);
        var result = await service.SaveProfileAsync(me, request, ct);
        return result.Ok ? OkResponse(result.Value) : QuoteHttp.Refuse(result);
    }

    public static Task<IResult> PutLogo(IFormFile? photo, ClaimsPrincipal principal, StayQuoteImageService service, HttpContext http, CancellationToken ct)
        => PutImage(QuoteImageKind.Logo, photo, principal, service, http, ct);

    public static Task<IResult> PutSignature(IFormFile? photo, ClaimsPrincipal principal, StayQuoteImageService service, HttpContext http, CancellationToken ct)
        => PutImage(QuoteImageKind.Signature, photo, principal, service, http, ct);

    public static Task<IResult> DeleteLogo(ClaimsPrincipal principal, StayQuoteImageService service, CancellationToken ct)
        => DeleteImage(QuoteImageKind.Logo, principal, service, ct);

    public static Task<IResult> DeleteSignature(ClaimsPrincipal principal, StayQuoteImageService service, CancellationToken ct)
        => DeleteImage(QuoteImageKind.Signature, principal, service, ct);

    // GET /image/{kind}: the caller's own re-encoded JPEG; any other kind is a plain 404.
    public static async Task<IResult> GetImage(
        string kind, ClaimsPrincipal principal, StayQuoteImageService service, IRateLimitService limiter, HttpContext http, CancellationToken ct)
    {
        if (!OwnerHttp.TryOwner(principal, out var me)) return UnauthorizedResponse();
        if (await OwnerHttp.ReadLimitAsync(limiter, http, OwnerRateLimitKeys.Read(me)) is { } denied) return denied;
        if (!QuoteImageKinds.TryParse(kind, out var parsed)) return NotFoundResponse();
        NoStore(http);
        var result = await service.ReadAsync(me, parsed, ct);
        return result.Ok ? Results.Bytes(result.Value!, "image/jpeg") : QuoteHttp.Refuse(result);
    }

    public static async Task<IResult> AcceptTerms(
        QuoteTermsAcceptRequest request, IValidator<QuoteTermsAcceptRequest> validator, ClaimsPrincipal principal,
        StayQuoteProfileService service, CancellationToken ct)
    {
        if (!OwnerHttp.TryOwner(principal, out var me)) return UnauthorizedResponse();
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid) return QuoteHttp.Invalid(validation);
        var result = await service.AcceptTermsAsync(me, request, ct);
        return result.Ok ? OkResponse(result.Value) : QuoteHttp.Refuse(result);
    }

    public static async Task<IResult> Reserve(
        QuoteReserveRequest request, IValidator<QuoteReserveRequest> validator, ClaimsPrincipal principal,
        StayQuoteReserveService service, HttpContext http, CancellationToken ct)
    {
        if (!OwnerHttp.TryOwner(principal, out var me)) return UnauthorizedResponse();
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid) return QuoteHttp.Invalid(validation);
        QuoteKinds.TryCode(request.Kind, out var kind);
        NoStore(http);
        var result = await service.ReserveAsync(me, request.DocumentId!.Value, kind, ct);
        return result.Ok ? OkResponse(result.Value) : QuoteHttp.Refuse(result);
    }

    public static async Task<IResult> RequestNameChange(
        QuoteNameChangeRequest request, IValidator<QuoteNameChangeRequest> validator, ClaimsPrincipal principal,
        StayQuoteProfileService service, CancellationToken ct)
    {
        if (!OwnerHttp.TryOwner(principal, out var me)) return UnauthorizedResponse();
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid) return QuoteHttp.Invalid(validation);
        var result = await service.RequestNameChangeAsync(me, request, ct);
        return result.Ok ? OkResponse(result.Value) : QuoteHttp.Refuse(result);
    }

    private static async Task<IResult> PutImage(
        QuoteImageKind kind, IFormFile? photo, ClaimsPrincipal principal, StayQuoteImageService service, HttpContext http, CancellationToken ct)
    {
        if (!OwnerHttp.TryOwner(principal, out var me)) return UnauthorizedResponse();
        if (photo == null || photo.Length == 0) return QuoteHttp.Refuse(QuoteErrors.PhotoInvalid, InvalidPhotoException.UserMessage);
        if (photo.Length > PhotoUpload.MaxBytes) return QuoteHttp.Refuse(QuoteErrors.PhotoTooLarge, PhotoUpload.TooBig);
        try
        {
            await using var stream = photo.OpenReadStream();
            if (!PhotoUpload.IsImage(stream)) return QuoteHttp.Refuse(QuoteErrors.PhotoInvalid, InvalidPhotoException.UserMessage);
            var result = await service.PutAsync(me, kind, stream, ct);
            return result.Ok ? OkResponse(result.Value) : QuoteHttp.Refuse(result);
        }
        catch (InvalidPhotoException)
        {
            return QuoteHttp.Refuse(QuoteErrors.PhotoInvalid, InvalidPhotoException.UserMessage);
        }
        catch (PhotoBusyException)
        {
            http.Response.Headers.RetryAfter = PhotoBusyException.RetryAfterSeconds.ToString();
            return QuoteHttp.Refuse(QuoteErrors.PhotoBusy, PhotoBusyException.UserMessage);
        }
    }

    private static async Task<IResult> DeleteImage(QuoteImageKind kind, ClaimsPrincipal principal, StayQuoteImageService service, CancellationToken ct)
    {
        if (!OwnerHttp.TryOwner(principal, out var me)) return UnauthorizedResponse();
        var result = await service.DeleteAsync(me, kind, ct);
        return result.Ok ? OkResponse(result.Value) : QuoteHttp.Refuse(result);
    }

    private static void NoStore(HttpContext http) => http.Response.Headers.CacheControl = "no-store";
}

// Typed refusals of the quotation routes: { type, message, data } with the status of QuoteErrors (or OwnerErrorTypes).
public static class QuoteHttp
{
    public static IResult Refuse<T>(QuoteResult<T> result) => result.ErrorType switch
    {
        StayQuoteProfileService.UnauthorizedType => UnauthorizedResponse(),
        OwnerErrorTypes.StaleEdit => OwnerHttp.Refuse(OwnerErrorTypes.StaleEdit),
        { } type => ErrorResponse(QuoteErrors.StatusOf(type), result.Message ?? QuoteErrors.MessageOf(type), type, result.Data),
        _ => ServerErrorResponse(),
    };

    public static IResult Refuse(string type, string? message = null, object? data = null)
        => ErrorResponse(QuoteErrors.StatusOf(type), message ?? QuoteErrors.MessageOf(type), type, data);

    // 400 ValidationError: the first static message, and data = { field: message } in camelCase (never a typed value).
    public static IResult Invalid(FluentValidation.Results.ValidationResult validation)
    {
        var fields = new Dictionary<string, string>();
        foreach (var error in validation.Errors)
        {
            var key = CamelPath(error.PropertyName);
            fields.TryAdd(key, error.ErrorMessage);
        }
        var message = validation.Errors.FirstOrDefault()?.ErrorMessage ?? QuoteErrors.MessageOf(QuoteErrors.ValidationError);
        return ErrorResponse(400, message, QuoteErrors.ValidationError, fields);
    }

    public static string CamelPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "request";
        return string.Join('.', path.Split('.').Select(p => p.Length == 0 ? p : char.ToLowerInvariant(p[0]) + p[1..]));
    }
}
