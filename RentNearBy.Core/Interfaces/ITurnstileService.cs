namespace RentNearBy.Core.Interfaces;

// Cloudflare Turnstile — the one gate in the web-enquiry flow that a scripted client (Postman/curl)
// cannot forge, since a valid token can only be produced by real JS running Cloudflare's challenge in an
// actual browser. VerifyAsync round-trips to Cloudflare's own siteverify API — this service never
// validates the token itself.
public interface ITurnstileService
{
    Task<bool> VerifyAsync(string token, string? remoteIp = null);
}
