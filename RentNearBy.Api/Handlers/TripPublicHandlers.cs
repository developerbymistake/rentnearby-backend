using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;

namespace RentNearBy.Api.Handlers;

// Public host page /go/confirm/{token}: anonymous, GET only renders, POST records the host's first answer.
public static class TripPublicHandlers
{
    private static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(1);

    public static async Task<IResult> Get(
        string token, HttpContext http, IUnitOfWork unitOfWork, IRateLimitService rateLimiter,
        IOptions<TripExecutionOptions> options, IConfiguration config)
    {
        Harden(http);
        if (await Throttled(http, rateLimiter)) return Page(http, 429, "Slow down", "<h1>Too many requests</h1><p>Too many requests. Try again in a minute.</p>", config);
        if (!options.Value.Enabled) return Paused(http, config);

        var e = await FindAnswerable(unitOfWork, token);
        if (e == null) return Used(http, config);

        var booking = ReadBooking(e);
        var traveller = FirstName(e);
        var body = new StringBuilder();
        body.Append($"<h1>{H(traveller)} says you agreed</h1>");
        body.Append("<div class=\"card\">");
        body.Append(Row("Stay", e.ListingName));
        body.Append(Row("Dates", Dates(e)));
        body.Append(Row("Price", $"Rs {(e.AgreedTotal ?? booking?.AgreedTotal ?? 0):N0}"));
        if (!string.IsNullOrWhiteSpace(booking?.RoomName)) body.Append(Row("Room", booking!.RoomName));
        body.Append("</div>");
        body.Append("<form method=\"post\">");
        body.Append("<button class=\"btn yes\" type=\"submit\" name=\"answer\" value=\"yes\">Yes, confirm</button>");
        body.Append("<button class=\"btn no\" type=\"submit\" name=\"answer\" value=\"notme\">Not me</button>");
        body.Append("</form>");
        body.Append($"<p class=\"note\">Your answer is shown to {H(traveller)}.</p>");
        body.Append("<p class=\"note\">Bakhli does not take payment. This only records what both of you say.</p>");

        return Page(http, 200, "Bakhli booking check", body.ToString(), config);
    }

    public static async Task<IResult> Post(
        string token, HttpContext http, IUnitOfWork unitOfWork, IRateLimitService rateLimiter,
        IOptions<TripExecutionOptions> options, IConfiguration config,
        ITripChatService chat, ITripNotifier notifier)
    {
        Harden(http);
        if (await Throttled(http, rateLimiter)) return Page(http, 429, "Slow down", "<h1>Too many requests</h1><p>Too many requests. Try again in a minute.</p>", config);
        if (!options.Value.Enabled) return Paused(http, config);

        string? answer = null;
        if (http.Request.HasFormContentType)
        {
            var form = await http.Request.ReadFormAsync();
            answer = form["answer"].ToString();
        }
        if (answer != "yes" && answer != "notme")
            return Page(http, 400, "Bakhli booking check", "<h1>Choose an answer</h1><p>Go back and tap Yes or Not me.</p>", config);

        var e = await FindAnswerable(unitOfWork, token);
        if (e == null) return Used(http, config);

        var confirm = answer == "yes";
        var posts = new List<TripChatPost>();
        HostAnswerOutcome outcome;
        await unitOfWork.BeginTransactionAsync();
        try
        {
            outcome = await TripHostAnswers.ApplyAsync(
                unitOfWork, chat, e, confirm, TripHostConfirmVia.Link, null, DateTime.UtcNow, posts);
            if (outcome == HostAnswerOutcome.Applied) await unitOfWork.SaveChangesAsync();
            await unitOfWork.CommitTransactionAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            await unitOfWork.RollbackTransactionAsync();
            return Used(http, config);
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }

        // Only a first answer counts; anything else (already answered, state moved) looks like an old link.
        if (outcome != HostAnswerOutcome.Applied) return Used(http, config);

        await chat.AfterCommitAsync(posts);
        await TripHostAnswers.SendPushesAsync(unitOfWork, notifier, e, confirm, TripHostConfirmVia.Link);

        return Page(http, 200, "Bakhli booking check",
            $"<h1>&#10003; Thank you</h1><p>{H(FirstName(e))} has been told.</p>", config);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    // Unknown, expired, answered and moved-on links all end up as the same null.
    private static async Task<TripEnquiry?> FindAnswerable(IUnitOfWork unitOfWork, string token)
    {
        var e = await unitOfWork.TripEnquiries.GetByTokenHashAsync(HostConfirmTokens.Hash(token));
        if (e == null) return null;
        if (e.HostConfirmTokenExpiresAt is not { } expires || expires < DateTime.UtcNow) return null;
        if (e.Status != TripEnquiryStatuses.ConfirmedByTraveller || e.HostConfirmAnsweredAt != null) return null;
        return e;
    }

    private static async Task<bool> Throttled(HttpContext http, IRateLimitService rateLimiter)
    {
        var ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var rl = await rateLimiter.CheckAsync($"confirm:{ip}", TripExecutionRules.ConfirmPagePerMinute, RateWindow);
        return !rl.IsAllowed;
    }

    private static void Harden(HttpContext http)
    {
        http.Response.Headers["X-Robots-Tag"] = "noindex";
        http.Response.Headers["Cache-Control"] = "no-store";
        http.Response.Headers["Referrer-Policy"] = "no-referrer";
    }

    private static TripPhoneBooking? ReadBooking(TripEnquiry e)
    {
        if (string.IsNullOrWhiteSpace(e.PhoneBookingJson)) return null;
        try { return JsonSerializer.Deserialize<TripPhoneBooking>(e.PhoneBookingJson, TripJson.Options); }
        catch (JsonException) { return null; }
    }

    private static string FirstName(TripEnquiry e)
    {
        try
        {
            var ctx = JsonSerializer.Deserialize<TripEnquiryContext>(e.ContextJson, TripJson.Options);
            var first = TripTextSafety.Clean(ctx?.TravellerName).Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.IsNullOrEmpty(first)) return first;
        }
        catch (JsonException) { }
        return "The guest";
    }

    private static string Dates(TripEnquiry e)
    {
        if (e.CheckIn is not { } a) return "-";
        var b = e.CheckOut ?? a.AddDays(1);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return $"{a.ToString("dd MMM", inv)} - {b.ToString("dd MMM", inv)}";
    }

    private static string Row(string label, string value) =>
        $"<div class=\"row\"><span>{H(label)}</span><strong>{H(value)}</strong></div>";

    private static string H(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);

    private static IResult Used(HttpContext http, IConfiguration config) =>
        Page(http, 200, "Bakhli booking check", "<h1>This link was already used</h1><p>Or it is not valid any more.</p>", config);

    private static IResult Paused(HttpContext http, IConfiguration config) =>
        Page(http, 503, "Bakhli booking check", "<h1>This link is paused</h1><p>This link is paused. Try later.</p>", config);

    private static IResult Page(HttpContext http, int status, string title, string bodyHtml, IConfiguration config)
    {
        var terms = H(config["AppLinks:TermsUrl"] ?? "https://developerbymistake.tech/terms");
        var privacy = H(config["AppLinks:PrivacyUrl"] ?? "https://developerbymistake.tech/privacy");
        var grievance = H(config["AppLinks:GrievanceUrl"] ?? "https://developerbymistake.tech/grievance");
        var html = $$"""
<!DOCTYPE html>
<html lang="en">
<head><meta charset="UTF-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<meta name="robots" content="noindex">
<meta property="og:title" content="Bakhli booking check">
<title>{{H(title)}}</title>
<style>body{font-family:sans-serif;max-width:480px;margin:0 auto;padding:0 20px 32px;color:#1e293b}header{padding:20px 0;font-weight:700;font-size:20px;color:#1e3a8a}h1{font-size:22px;margin:8px 0 16px}.card{background:#f1f5f9;border-radius:12px;padding:8px 16px;margin:16px 0}.row{display:flex;justify-content:space-between;gap:12px;padding:10px 0;border-bottom:1px solid #e2e8f0}.row:last-child{border-bottom:0}.row span{color:#64748b}.row strong{text-align:right;word-break:break-word}.btn{display:block;width:100%;height:52px;border:0;border-radius:12px;color:#fff;font-size:16px;font-weight:600;margin:10px 0}.yes{background:#059669}.no{background:#DC2626}.note{color:#64748b;font-size:13px}footer{margin-top:28px;font-size:13px}footer a{color:#2563eb;margin-right:8px}</style>
</head>
<body>
<header>Bakhli</header>
{{bodyHtml}}
<footer><a href="{{terms}}">Terms</a><a href="{{privacy}}">Privacy</a><a href="{{grievance}}">Grievance contact</a></footer>
</body>
</html>
""";
        http.Response.StatusCode = status;
        return Results.Content(html, "text/html; charset=utf-8", Encoding.UTF8);
    }
}
