using Google.Apis.Auth;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RentNearBy.Core.Models;

namespace RentNearBy.Infrastructure.Services;

public sealed record PlayRtdnAuthResult(bool Ok, string? Email);

// Pub/Sub push OIDC token: Google signature, aud = the RTDN URL, email = the push service account, email_verified.
public interface IPlayRtdnAuthenticator
{
    Task<PlayRtdnAuthResult> ValidateAsync(string? authorizationHeader);
}

public sealed class PlayRtdnAuthenticator(IOptions<PlayBillingOptions> options, ILogger<PlayRtdnAuthenticator> logger)
    : IPlayRtdnAuthenticator
{
    private const string Bearer = "Bearer ";

    public async Task<PlayRtdnAuthResult> ValidateAsync(string? authorizationHeader)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.RtdnAudience) || string.IsNullOrWhiteSpace(o.RtdnServiceAccountEmail))
            return new PlayRtdnAuthResult(false, null);
        if (string.IsNullOrWhiteSpace(authorizationHeader)
            || !authorizationHeader.StartsWith(Bearer, StringComparison.OrdinalIgnoreCase))
            return new PlayRtdnAuthResult(false, null);

        var jwt = authorizationHeader[Bearer.Length..].Trim();
        try
        {
            var payload = await GoogleJsonWebSignature.ValidateAsync(jwt, new GoogleJsonWebSignature.ValidationSettings
            {
                Audience = [o.RtdnAudience],
            });
            var ok = payload.EmailVerified
                && string.Equals(payload.Email, o.RtdnServiceAccountEmail, StringComparison.OrdinalIgnoreCase);
            return new PlayRtdnAuthResult(ok, payload.Email);
        }
        catch (InvalidJwtException ex)
        {
            logger.LogDebug("Play RTDN token rejected: {Message}", ex.Message);
            return new PlayRtdnAuthResult(false, null);
        }
    }
}
