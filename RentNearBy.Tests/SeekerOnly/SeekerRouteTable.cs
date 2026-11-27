using System.Security.Claims;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RentNearBy.Api.Extensions;
using RentNearBy.Api.Validators;

namespace RentNearBy.Tests.SeekerOnly;

// The real /api/v1 route table, mapped by ApiRouteMap exactly as Program.cs maps it, and the seeker-only contract
// (section 1) as explicit lists: every route sits in exactly one list.
public static class SeekerRouteTable
{
    public sealed record Built(string Key, IReadOnlyList<IAuthorizeData> Authorize, bool AllowAnonymous)
    {
        public IReadOnlyList<string?> Policies => Authorize.Select(a => a.Policy).ToList();
    }

    private static readonly Lazy<IReadOnlyDictionary<string, Built>> Table = new(BuildTable);

    public static IReadOnlyDictionary<string, Built> Routes => Table.Value;

    public static Built Route(string key) => Assert.Contains(key, Routes);

    private static IReadOnlyDictionary<string, Built> BuildTable()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DATABASE_URL"] = "Host=localhost;Database=test;Username=test;Password=test",
            ["Jwt:Key"] = "unit-test-signing-key-unit-test-signing-key",
        });
        builder.Services.AddApplicationServices(builder.Configuration);
        builder.Services.AddJwtAuthentication(builder.Configuration);
        builder.Services.AddSignalR();
        builder.Services.AddValidatorsFromAssemblyContaining<SendOtpRequestValidator>();
        using var app = builder.Build();
        app.MapApiRoutes();

        var table = new Dictionary<string, Built>();
        foreach (var e in ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>())
        {
            var template = "/" + e.RoutePattern.RawText!.Trim('/');
            if (!template.StartsWith("/api/v1/", StringComparison.Ordinal)) continue;
            foreach (var method in e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [])
            {
                var key = $"{method} {template}";
                Assert.False(table.ContainsKey(key), $"{key} is mapped twice");
                table[key] = new Built(key, e.Metadata.GetOrderedMetadata<IAuthorizeData>().ToList(), e.Metadata.GetMetadata<IAllowAnonymous>() != null);
            }
        }
        return table;
    }

    public static ClaimsPrincipal User(string? role, params Claim[] extra)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new("session_id", Guid.NewGuid().ToString()), new("actor_type", "user") };
        if (role != null) claims.Add(new Claim(ClaimTypes.Role, role));
        claims.AddRange(extra);
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    public static ClaimsPrincipal AdminUser()
        => new(new ClaimsIdentity([new Claim("session_id", Guid.NewGuid().ToString()), new Claim("actor_type", "admin")], "test"));

    private static readonly Lazy<ServiceProvider> AuthServices = new(() => new ServiceCollection()
        .AddLogging()
        .AddAuthorization(AuthenticationExtensions.ConfigurePolicies)
        .BuildServiceProvider());

    public static IAuthorizationService Authorization => AuthServices.Value.GetRequiredService<IAuthorizationService>();

    // The policy the authorization middleware builds for the route from its metadata (null = no authorization at all).
    public static Task<AuthorizationPolicy?> PolicyOf(Built route)
        => AuthorizationPolicy.CombineAsync(AuthServices.Value.GetRequiredService<IAuthorizationPolicyProvider>(), route.Authorize);

    public static async Task<bool> AllowsAsync(Built route, ClaimsPrincipal user)
    {
        var policy = await PolicyOf(route);
        return policy == null || (await Authorization.AuthorizeAsync(user, null, policy)).Succeeded;
    }

    // 7 routes
    public static readonly string[] TripsCore =
    [
        "GET /api/v1/trips",
        "POST /api/v1/trips",
        "GET /api/v1/trips/{id:guid}",
        "GET /api/v1/trips/{id:guid}/invite",
        "PUT /api/v1/trips/{id:guid}",
        "POST /api/v1/trips/{id:guid}/complete-plan",
        "DELETE /api/v1/trips/{id:guid}",
    ];

    // 4 routes
    public static readonly string[] PlannerReads =
    [
        "GET /api/v1/trips/cities/{cityKey:long}/spots",
        "POST /api/v1/trips/cities/{cityKey:long}/spots/minutes",
        "GET /api/v1/trips/cities/{cityKey:long}/supply",
        "POST /api/v1/trips/plan",
    ];

    // 13 routes
    public static readonly string[] ExecutionTravellerSide =
    [
        "POST /api/v1/trips/{tripId:guid}/enquiries",
        "POST /api/v1/trips/enquiries/{id:guid}/withdraw",
        "POST /api/v1/trips/enquiries/{id:guid}/restore",
        "GET /api/v1/trips/enquiries/mine",
        "GET /api/v1/trips/enquiries/mine/count",
        "GET /api/v1/trips/{tripId:guid}/steps/{stepKey}/similar-stays",
        "POST /api/v1/trips/enquiries/{id:guid}/refresh-context",
        "POST /api/v1/trips/enquiries/{id:guid}/lead-update",
        "POST /api/v1/trips/enquiries/{id:guid}/offers/{offerId:guid}/accept",
        "POST /api/v1/trips/enquiries/{id:guid}/offers/{offerId:guid}/decline",
        "POST /api/v1/trips/enquiries/{id:guid}/counter",
        "POST /api/v1/trips/enquiries/{id:guid}/change-request",
        "POST /api/v1/trips/{tripId:guid}/taxi-bookings",
    ];

    // 5 routes
    public static readonly string[] Shortlist =
    [
        "GET /api/v1/trips/{tripId:guid}/shortlist",
        "PUT /api/v1/trips/{tripId:guid}/steps/{stepKey}/shortlist/{listingId:guid}",
        "DELETE /api/v1/trips/{tripId:guid}/steps/{stepKey}/shortlist/{listingId:guid}",
        "POST /api/v1/trips/{tripId:guid}/steps/{stepKey}/shortlist/close",
        "POST /api/v1/trips/{tripId:guid}/steps/{stepKey}/mark-booked",
    ];

    // 4 routes
    public static readonly string[] Lifecycle =
    [
        "POST /api/v1/trips/{tripId:guid}/problems",
        "POST /api/v1/trips/{tripId:guid}/complete",
        "PUT /api/v1/trips/{tripId:guid}/mute",
        "GET /api/v1/trips/{tripId:guid}/summary",
    ];

    // 13 routes
    public static readonly string[] Sharing =
    [
        "GET /api/v1/trips/shared",
        "GET /api/v1/trips/slots",
        "GET /api/v1/trips/invite-options",
        "GET /api/v1/trips/invites/{memberId:guid}",
        "POST /api/v1/trips/invites/{memberId:guid}/accept",
        "POST /api/v1/trips/invites/{memberId:guid}/decline",
        "GET /api/v1/trips/{id:guid}/members",
        "DELETE /api/v1/trips/{id:guid}/members/{userId:guid}",
        "GET /api/v1/trips/{id:guid}/members/{userId:guid}/expense-count",
        "POST /api/v1/trips/{id:guid}/invites",
        "DELETE /api/v1/trips/{id:guid}/invites/{userId:guid}",
        "POST /api/v1/trips/{id:guid}/leave",
        "GET /api/v1/trips/{id:guid}/view",
    ];

    // 9 routes
    public static readonly string[] Expenses =
    [
        "GET /api/v1/trips/{tripId:guid}/expenses",
        "GET /api/v1/trips/{tripId:guid}/expenses/summary",
        "POST /api/v1/trips/{tripId:guid}/expenses/suggestions/skip",
        "POST /api/v1/trips/{tripId:guid}/expenses/late-join/{userId:guid}/keep",
        "POST /api/v1/trips/{tripId:guid}/expenses/share-with",
        "GET /api/v1/trips/{tripId:guid}/expenses/{expenseId:guid}",
        "PUT /api/v1/trips/{tripId:guid}/expenses/{expenseId:guid}",
        "DELETE /api/v1/trips/{tripId:guid}/expenses/{expenseId:guid}",
        "GET /api/v1/trips/{tripId:guid}/balances",
    ];

    // 5 routes
    public static readonly string[] Payments =
    [
        "POST /api/v1/trips/{tripId:guid}/payments/said-paid",
        "POST /api/v1/trips/{tripId:guid}/payments/remind",
        "POST /api/v1/trips/{tripId:guid}/payments/{paymentId:guid}/received",
        "POST /api/v1/trips/{tripId:guid}/payments/{paymentId:guid}/withdraw",
        "POST /api/v1/trips/{tripId:guid}/payments/{paymentId:guid}/not-received",
    ];

    // 3 routes
    public static readonly string[] TaxiRequestOnTrip =
    [
        "GET /api/v1/trips/{tripId:guid}/taxi-request/pickup-places",
        "POST /api/v1/trips/{tripId:guid}/taxi-request",
        "GET /api/v1/trips/{tripId:guid}/taxi-request",
    ];

    // 7 routes
    public static readonly string[] TaxiRequest =
    [
        "GET /api/v1/taxi-requests/{requestId:guid}",
        "POST /api/v1/taxi-requests/{requestId:guid}/cancel",
        "POST /api/v1/taxi-requests/{requestId:guid}/ask-more",
        "POST /api/v1/taxi-requests/{requestId:guid}/offers/{offerId:guid}/select",
        "POST /api/v1/taxi-requests/{requestId:guid}/offers/{offerId:guid}/not-agreed",
        "POST /api/v1/taxi-requests/{requestId:guid}/end",
        "POST /api/v1/taxi-requests/{requestId:guid}/rating",
    ];

    // 4 routes (6 buddy request routes retired by social D3)
    public static readonly string[] Buddies =
    [
        "POST /api/v1/buddies/find",
        "GET /api/v1/buddies",
        "GET /api/v1/buddies/summary",
        "DELETE /api/v1/buddies/{userId:guid}",
    ];

    // 6 routes
    public static readonly string[] PersonSafety =
    [
        "POST /api/v1/users/{userId:guid}/block",
        "POST /api/v1/users/{userId:guid}/unblock",
        "POST /api/v1/users/{userId:guid}/report",
        "GET /api/v1/users/me/blocks",
        "GET /api/v1/users/{userId:guid}/block-context",
        "GET /api/v1/report-reasons",
    ];

    // 8 routes
    public static readonly string[] Journeys =
    [
        "POST /api/v1/taxis/ways/ask",
        "POST /api/v1/taxis/journeys",
        "GET /api/v1/taxis/journeys",
        "GET /api/v1/taxis/journeys/{id:guid}",
        "DELETE /api/v1/taxis/journeys/{id:guid}",
        "POST /api/v1/taxis/journeys/{id:guid}/refresh",
        "PUT /api/v1/taxis/journeys/{id:guid}/legs/{legIndex:int}/driver",
        "PUT /api/v1/taxis/journeys/{id:guid}/status",
    ];

    // 36 routes: Bakhli social (CONTRACT_SOCIAL section 2), every one SeekerOnly
    public static readonly string[] Social =
    [
        "GET /api/v1/social/me",
        "PUT /api/v1/social/me/profile",
        "PUT /api/v1/social/me/privacy",
        "PUT /api/v1/social/me/area",
        "POST /api/v1/social/me/guidelines",
        "GET /api/v1/social/travellers/{userId:guid}",
        "PUT /api/v1/social/travellers/{userId:guid}/follow",
        "DELETE /api/v1/social/travellers/{userId:guid}/follow",
        "GET /api/v1/social/followers",
        "GET /api/v1/social/following",
        "DELETE /api/v1/social/followers/{userId:guid}",
        "GET /api/v1/social/follow-requests",
        "POST /api/v1/social/follow-requests/{userId:guid}/accept",
        "POST /api/v1/social/follow-requests/{userId:guid}/decline",
        "PUT /api/v1/social/buddies/{userId:guid}",
        "GET /api/v1/social/trips/feed",
        "GET /api/v1/social/trips/rail",
        "GET /api/v1/social/trips/{tripId:guid}",
        "POST /api/v1/social/trips",
        "PUT /api/v1/social/trips/{tripId:guid}/post",
        "DELETE /api/v1/social/trips/{tripId:guid}/post",
        "POST /api/v1/social/trips/{tripId:guid}/join-requests",
        "DELETE /api/v1/social/trips/{tripId:guid}/join-requests/mine",
        "GET /api/v1/social/trips/{tripId:guid}/join-requests",
        "POST /api/v1/social/join-requests/{id:guid}/accept",
        "POST /api/v1/social/join-requests/{id:guid}/decline",
        "GET /api/v1/social/chat/rooms",
        "GET /api/v1/social/chat/with/{userId:guid}",
        "GET /api/v1/social/chat/trip/{tripId:guid}",
        "GET /api/v1/social/chat/rooms/{roomId:guid}",
        "POST /api/v1/social/chat/rooms",
        "GET /api/v1/social/chat/rooms/{roomId:guid}/messages",
        "POST /api/v1/social/chat/rooms/{roomId:guid}/messages",
        "POST /api/v1/social/chat/rooms/{roomId:guid}/read",
        "DELETE /api/v1/social/chat/rooms/{roomId:guid}",
        "POST /api/v1/social/reports",
    ];

    // 1 routes
    public static readonly string[] SeekerFeed =
    [
        "GET /api/v1/home/seeker-feed",
    ];

    // 74 routes
    public static readonly string[] OpenUser =
    [
        "POST /api/v1/auth/send-otp",
        "POST /api/v1/auth/verify-phone",
        "POST /api/v1/auth/logout",
        "GET /api/v1/users/profile",
        "PUT /api/v1/users/profile/name",
        "PUT /api/v1/users/profile/contact-visibility",
        "PUT /api/v1/users/me/photo",
        "DELETE /api/v1/users/me/photo",
        "GET /api/v1/users/reports",
        "PUT /api/v1/users/me/role-choice",
        "PUT /api/v1/users/me/role-correction",
        "POST /api/v1/users/me/role-request",
        "GET /api/v1/users/me/role-request",
        "GET /api/v1/roles",
        "POST /api/v1/stays/{id:guid}/report",
        "GET /api/v1/taxis/route-feed",
        "GET /api/v1/taxis/ways",
        "GET /api/v1/taxis/ways/available",
        "GET /api/v1/taxis/ways/plans/{planKey}",
        "GET /api/v1/taxis/ways/legs/{legKey}/drivers",
        "POST /api/v1/taxis/{id:guid}/report",
        "DELETE /api/v1/account",
        "POST /api/v1/notifications/register-token",
        "DELETE /api/v1/notifications/register-token",
        "GET /api/v1/notifications",
        "GET /api/v1/notifications/unread-count",
        "PUT /api/v1/notifications/{id:guid}/read",
        "PUT /api/v1/notifications/read-all",
        "GET /api/v1/banners/active",
        "POST /api/v1/banners/{id:guid}/dismiss",
        "GET /api/v1/chat/conversations",
        "POST /api/v1/chat/conversations",
        "GET /api/v1/chat/conversations/{conversationId:guid}/messages",
        "POST /api/v1/chat/conversations/{conversationId:guid}/messages",
        "POST /api/v1/chat/conversations/{conversationId:guid}/read",
        "GET /api/v1/chat/unread-count",
        "POST /api/v1/chat/messages/{messageId:guid}/contact-response",
        "POST /api/v1/chat/messages/{messageId:guid}/schedule-response",
        "POST /api/v1/chat/users/{userId:guid}/block",
        "POST /api/v1/chat/users/{userId:guid}/unblock",
        "GET /api/v1/chat/question-templates",
        "GET /api/v1/plans/me",
        "POST /api/v1/plans/quote",
        "POST /api/v1/plans/purchases",
        "GET /api/v1/plans/purchases",
        "GET /api/v1/plans/purchases/open",
        "GET /api/v1/plans/purchases/{id:guid}",
        "POST /api/v1/plans/purchases/{id:guid}/verify",
        "POST /api/v1/plans/purchases/{id:guid}/verify-play",
        "POST /api/v1/plans/purchases/restore",
        "POST /api/v1/plans/trial",
        "GET /api/v1/trips/cities",
        "GET /api/v1/trips/cities/nearest",
        "GET /api/v1/trips/cities/version",
        "GET /api/v1/trips/cities/{cityKey:long}/spots/{spotId}",
        "GET /api/v1/trips/map-packs/{stateId:guid}/{version}",
        "GET /api/v1/trips/enquiries/by-conversation/{conversationId:guid}",
        "GET /api/v1/trips/enquiries/{id:guid}",
        "POST /api/v1/trips/enquiries/{id:guid}/cancel",
        "POST /api/v1/trips/enquiries/{id:guid}/offers",
        "POST /api/v1/trips/enquiries/{id:guid}/not-available",
        "POST /api/v1/trips/enquiries/{id:guid}/share-details",
        "POST /api/v1/trips/enquiries/{id:guid}/revised-offer",
        "POST /api/v1/trips/enquiries/{id:guid}/change-request/{offerId:guid}/accept",
        "POST /api/v1/trips/enquiries/{id:guid}/report-traveller",
        "POST /api/v1/trips/leads/{id:guid}/fare",
        "POST /api/v1/trips/leads/{id:guid}/not-available",
        "POST /api/v1/trips/leads/{id:guid}/confirm",
        "POST /api/v1/trips/leads/{id:guid}/deny",
        "GET /api/v1/notifications/notices",
        "PUT /api/v1/notifications/notices/{id:guid}/read",
        "GET /api/v1/festivals",
        "GET /api/v1/owner/home",
        "PUT /api/v1/social/me/onboarding",
    ];

    // 57 routes (42 + 4 stay enquiry ring + 11 stay quotation)
    public static readonly string[] OwnerStay =
    [
        "PUT /api/v1/stays/me/basic",
        "POST /api/v1/stays/me/photos",
        "DELETE /api/v1/stays/me/photos/{photoId:guid}",
        "PUT /api/v1/stays/me/photos/{photoId:guid}",
        "PUT /api/v1/stays/me/rooms",
        "PUT /api/v1/stays/me/rooms/{roomId:guid}/price",
        "DELETE /api/v1/stays/me/rooms/{roomId:guid}",
        "PUT /api/v1/stays/me/rules",
        "POST /api/v1/stays/me/submit",
        "PUT /api/v1/stays/me/accepting",
        "PUT /api/v1/stays/me/share-details",
        "DELETE /api/v1/stays/me",
        "GET /api/v1/stays/{id:guid}/reports",
        "GET /api/v1/stay-jobs/me",
        "GET /api/v1/stay-jobs/me/{id:guid}",
        "POST /api/v1/stay-jobs/me",
        "PUT /api/v1/stay-jobs/me/{id:guid}",
        "PUT /api/v1/stay-jobs/me/{id:guid}/close",
        "GET /api/v1/trips/enquiries/owner",
        "GET /api/v1/stays/me/calendar",
        "GET /api/v1/stays/me/calendar/upcoming",
        "GET /api/v1/stays/me/calendar/past",
        "GET /api/v1/stays/me/calendar/entries/{id:guid}",
        "POST /api/v1/stays/me/calendar/entries",
        "PUT /api/v1/stays/me/calendar/entries/{id:guid}",
        "DELETE /api/v1/stays/me/calendar/entries/{id:guid}",
        "PUT /api/v1/stays/me/calendar/entries/{id:guid}/money",
        "GET /api/v1/stays/me/calendar/summary",
        "GET /api/v1/stays/me/calendar/balance-due",
        "PUT /api/v1/stays/me/calendar/reminder",
        "GET /api/v1/stays/me/blocked-ranges",
        "POST /api/v1/stays/me/blocked-ranges",
        "PUT /api/v1/stays/me/blocked-ranges/{id:guid}",
        "DELETE /api/v1/stays/me/blocked-ranges/{id:guid}",
        "GET /api/v1/stays/me/special-prices",
        "POST /api/v1/stays/me/special-prices",
        "PUT /api/v1/stays/me/special-prices/{id:guid}",
        "DELETE /api/v1/stays/me/special-prices/{id:guid}",
        "PUT /api/v1/stays/me/special-prices/{id:guid}/pause",
        "PUT /api/v1/stays/me/special-prices/{id:guid}/live",
        "GET /api/v1/stays/me",
        "PUT /api/v1/stays/me/plan-visible",
        "GET /api/v1/stays/me/enquiry-ring",
        "PUT /api/v1/stays/me/enquiry-ring",
        "POST /api/v1/stays/me/enquiry-ring/test",
        "POST /api/v1/stays/me/enquiry-ring/{enquiryId:guid}/answer",
        // Stay quotation (BUILD_PLAN 4.1).
        "GET /api/v1/stays/me/quotation",
        "GET /api/v1/stays/me/quotation/profile/bank",
        "PUT /api/v1/stays/me/quotation/profile",
        "PUT /api/v1/stays/me/quotation/logo",
        "PUT /api/v1/stays/me/quotation/signature",
        "DELETE /api/v1/stays/me/quotation/logo",
        "DELETE /api/v1/stays/me/quotation/signature",
        "GET /api/v1/stays/me/quotation/image/{kind}",
        "POST /api/v1/stays/me/quotation/terms/accept",
        "POST /api/v1/stays/me/quotation/reserve",
        "POST /api/v1/stays/me/quotation/name-change",
    ];

    // 39 routes
    public static readonly string[] OwnerTaxi =
    [
        "PUT /api/v1/taxis/me/basic",
        "POST /api/v1/taxis/me/plate-photo",
        "POST /api/v1/taxis/me/display-photo",
        "PUT /api/v1/taxis/me/routes",
        "PUT /api/v1/taxis/me/routes/{routeId:guid}/fare",
        "DELETE /api/v1/taxis/me/routes/{routeId:guid}",
        "PUT /api/v1/taxis/me/local-rate",
        "POST /api/v1/taxis/me/submit",
        "PUT /api/v1/taxis/me/availability",
        "DELETE /api/v1/taxis/me",
        "GET /api/v1/taxis/{id:guid}/reports",
        "GET /api/v1/trips/leads/driver",
        "GET /api/v1/taxi-requests/driver/offers",
        "GET /api/v1/taxi-requests/driver/offers/{offerId:guid}",
        "POST /api/v1/taxi-requests/driver/offers/{offerId:guid}/accept",
        "POST /api/v1/taxi-requests/driver/offers/{offerId:guid}/decline",
        "POST /api/v1/taxi-requests/driver/offers/{offerId:guid}/confirm",
        "POST /api/v1/taxi-requests/driver/offers/{offerId:guid}/withdraw",
        "POST /api/v1/taxi-requests/driver/bookings/{bookingId:guid}/end",
        "GET /api/v1/taxi-requests/driver/bookings",
        "GET /api/v1/taxi-requests/driver/rating",
        "GET /api/v1/taxi-requests/driver/readiness",
        "PUT /api/v1/taxi-requests/driver/readiness",
        "POST /api/v1/taxi-requests/driver/test-ring",
        "GET /api/v1/taxis/me/calendar",
        "GET /api/v1/taxis/me/calendar/upcoming",
        "GET /api/v1/taxis/me/calendar/past",
        "GET /api/v1/taxis/me/calendar/entries/{id:guid}",
        "POST /api/v1/taxis/me/calendar/entries",
        "PUT /api/v1/taxis/me/calendar/entries/{id:guid}",
        "DELETE /api/v1/taxis/me/calendar/entries/{id:guid}",
        "PUT /api/v1/taxis/me/calendar/entries/{id:guid}/money",
        "GET /api/v1/taxis/me/calendar/summary",
        "GET /api/v1/taxis/me/calendar/balance-due",
        "PUT /api/v1/taxis/me/calendar/reminder",
        "GET /api/v1/taxis/me/days-off",
        "PUT /api/v1/taxis/me/days-off",
        "GET /api/v1/taxis/me",
        "PUT /api/v1/taxis/me/plan-visible",
    ];

    // 127 routes
    public static readonly string[] Admin =
    [
        "POST /api/v1/admin-auth/logout",
        "POST /api/v1/admin-auth/register-token",
        "DELETE /api/v1/admin-auth/register-token",
        "POST /api/v1/admin/districts",
        "PUT /api/v1/admin/districts/{id:guid}/active",
        "PUT /api/v1/admin/districts/{id:guid}/business-onboarding",
        "PUT /api/v1/admin/districts/{id:guid}/force-active",
        "DELETE /api/v1/admin/districts/{id:guid}",
        "PUT /api/v1/admin/states/{stateName}/active",
        "PUT /api/v1/admin/states/{stateName}/business-onboarding",
        "POST /api/v1/admin/cities",
        "DELETE /api/v1/admin/cities/{id:guid}",
        "POST /api/v1/admin/report-reasons",
        "PUT /api/v1/admin/report-reasons/{id:guid}",
        "DELETE /api/v1/admin/report-reasons/{id:guid}",
        "GET /api/v1/admin/stats",
        "GET /api/v1/admin/trips/enquiries",
        "GET /api/v1/admin/trips/enquiries/{id:guid}",
        "GET /api/v1/admin/trips/problems",
        "POST /api/v1/admin/trips/enquiries/{id:guid}/close",
        "GET /api/v1/admin/geocode",
        "GET /api/v1/admin/users",
        "GET /api/v1/admin/deleted-accounts",
        "GET /api/v1/admin/deleted-accounts/{id:guid}",
        "GET /api/v1/admin/users/{id:guid}",
        "PUT /api/v1/admin/users/{id:guid}/status",
        "DELETE /api/v1/admin/users/{id:guid}/profile-photo",
        "GET /api/v1/admin/users/{id:guid}/plans",
        "POST /api/v1/admin/users/{id:guid}/plan/grant",
        "POST /api/v1/admin/users/{id:guid}/plan/revoke",
        "PUT /api/v1/admin/users/{id:guid}/role",
        "GET /api/v1/admin/role-requests",
        "PUT /api/v1/admin/role-requests/{id:guid}/approve",
        "PUT /api/v1/admin/role-requests/{id:guid}/reject",
        "GET /api/v1/admin/purchases",
        "GET /api/v1/admin/purchases/{id:guid}",
        "POST /api/v1/admin/purchases/{id:guid}/recheck",
        "POST /api/v1/admin/purchases/{id:guid}/refund",
        "GET /api/v1/admin/purchases/{id:guid}/play-token",
        "GET /api/v1/admin/payment-feature",
        "PUT /api/v1/admin/payment-feature",
        "GET /api/v1/admin/trip-execution",
        "PUT /api/v1/admin/trip-execution",
        "GET /api/v1/admin/map-discovery",
        "PUT /api/v1/admin/map-discovery",
        "GET /api/v1/admin/app-tabs",
        "PUT /api/v1/admin/app-tabs/{tabKey}",
        "GET /api/v1/admin/app-version",
        "PUT /api/v1/admin/app-version/{platform}",
        "GET /api/v1/admin/coupons",
        "GET /api/v1/admin/coupons/{id:guid}",
        "POST /api/v1/admin/coupons",
        "PUT /api/v1/admin/coupons/{id:guid}",
        "POST /api/v1/admin/coupons/{id:guid}/revoke",
        "GET /api/v1/admin/coupons/{id:guid}/redemptions",
        "GET /api/v1/admin/plans",
        "POST /api/v1/admin/plans",
        "PUT /api/v1/admin/plans/{id:guid}",
        "GET /api/v1/admin/reports",
        "GET /api/v1/admin/reports/{id:guid}",
        "PUT /api/v1/admin/reports/{id:guid}/resolve",
        "PUT /api/v1/admin/reports/{id:guid}/reject",
        "POST /api/v1/admin/broadcast-notification",
        "POST /api/v1/admin/question-templates",
        "PUT /api/v1/admin/question-templates/{id:guid}",
        "PUT /api/v1/admin/question-templates/{id:guid}/active",
        "POST /api/v1/admin/stays/types",
        "PUT /api/v1/admin/stays/types/{id:guid}",
        "DELETE /api/v1/admin/stays/types/{id:guid}",
        "GET /api/v1/admin/stays/registration-types",
        "POST /api/v1/admin/stays/registration-types",
        "PUT /api/v1/admin/stays/registration-types/{id:guid}",
        "DELETE /api/v1/admin/stays/registration-types/{id:guid}",
        "GET /api/v1/admin/stays",
        "GET /api/v1/admin/stays/{id:guid}",
        "PUT /api/v1/admin/stays/{id:guid}/approve",
        "PUT /api/v1/admin/stays/{id:guid}/reject",
        "PUT /api/v1/admin/stays/{id:guid}/unpublish",
        "DELETE /api/v1/admin/stays/{id:guid}",
        "GET /api/v1/admin/stay-jobs",
        "PUT /api/v1/admin/stay-jobs/{id:guid}/close",
        "DELETE /api/v1/admin/stay-jobs/{id:guid}",
        "GET /api/v1/admin/stay-jobs/lookups",
        "POST /api/v1/admin/stay-jobs/lookups/{kind}",
        "PUT /api/v1/admin/stay-jobs/lookups/{kind}/{id:guid}",
        "DELETE /api/v1/admin/stay-jobs/lookups/{kind}/{id:guid}",
        "GET /api/v1/admin/taxis/vehicle-types",
        "POST /api/v1/admin/taxis/vehicle-types",
        "PUT /api/v1/admin/taxis/vehicle-types/{id:guid}",
        "DELETE /api/v1/admin/taxis/vehicle-types/{id:guid}",
        "GET /api/v1/admin/taxis/popular-routes",
        "POST /api/v1/admin/taxis/popular-routes",
        "PUT /api/v1/admin/taxis/popular-routes/order",
        "DELETE /api/v1/admin/taxis/popular-routes/{id:guid}",
        "PUT /api/v1/admin/taxis/popular-routes/{id:guid}/active",
        "GET /api/v1/admin/taxis",
        "GET /api/v1/admin/taxis/{id:guid}",
        "PUT /api/v1/admin/taxis/{id:guid}/approve",
        "PUT /api/v1/admin/taxis/{id:guid}/reject",
        "PUT /api/v1/admin/taxis/{id:guid}/unpublish",
        "DELETE /api/v1/admin/taxis/{id:guid}",
        "DELETE /api/v1/admin/taxis/{id:guid}/display-photo",
        "GET /api/v1/admin/district-banners/check",
        "GET /api/v1/admin/district-banners",
        "POST /api/v1/admin/district-banners",
        "PUT /api/v1/admin/district-banners/{id:guid}/active",
        "DELETE /api/v1/admin/district-banners/{id:guid}",
        "GET /api/v1/admin/taxi-requests",
        "GET /api/v1/admin/taxi-requests/health",
        "GET /api/v1/admin/taxi-requests/{id:guid}",
        "POST /api/v1/admin/taxi-requests/{id:guid}/cancel",
        "GET /api/v1/admin/taxi-bookings",
        "PUT /api/v1/admin/users/{id:guid}/buddy-hold",
        "GET /api/v1/admin/festivals",
        "GET /api/v1/admin/festivals/states",
        "POST /api/v1/admin/festivals",
        "PUT /api/v1/admin/festivals/{id:guid}",
        "DELETE /api/v1/admin/festivals/{id:guid}",
        "GET /api/v1/admin/plans/keys",
        "GET /api/v1/admin/plans/{id:guid}",
        "GET /api/v1/admin/reports/special-prices",
        "GET /api/v1/admin/stays/pending-edits",
        "POST /api/v1/admin/stays/{id:guid}/pending-edit/approve",
        "POST /api/v1/admin/stays/{id:guid}/pending-edit/reject",
        "GET /api/v1/admin/reports/{id:guid}/snapshot",
        "POST /api/v1/admin/social/trips/{tripId:guid}/unpublish",
        "PUT /api/v1/admin/users/{id:guid}/identity",
    ];

    // 45 routes
    public static readonly string[] Anonymous =
    [
        "POST /api/v1/auth/phone/send-otp",
        "POST /api/v1/auth/phone/verify-otp",
        "POST /api/v1/auth/phone/complete-onboarding",
        "POST /api/v1/admin-auth/login",
        "POST /api/v1/admin-auth/send-reset-otp",
        "POST /api/v1/admin-auth/reset-password",
        "GET /api/v1/locations/context",
        "GET /api/v1/locations/districts",
        "GET /api/v1/locations/districts/{id:guid}/boundary",
        "GET /api/v1/locations/cities",
        "GET /api/v1/admin/districts",
        "GET /api/v1/admin/states",
        "GET /api/v1/admin/cities",
        "GET /api/v1/admin/report-reasons",
        "GET /api/v1/admin/question-templates",
        "GET /api/v1/stays/types",
        "GET /api/v1/stays/registration-types",
        "GET /api/v1/stays/browse",
        "GET /api/v1/stays/nearby",
        "GET /api/v1/stays/nearest",
        "GET /api/v1/stays/feed",
        "GET /api/v1/stays/by-slug/{slug}",
        "GET /api/v1/stays/{id:guid}",
        "GET /api/v1/admin/stays/types",
        "GET /api/v1/stay-jobs/lookups",
        "GET /api/v1/stay-jobs",
        "GET /api/v1/stay-jobs/feed",
        "GET /api/v1/stay-jobs/{id:guid}",
        "GET /api/v1/taxis/vehicle-types",
        "GET /api/v1/taxis/browse",
        "GET /api/v1/taxis/search",
        "GET /api/v1/taxis/feed",
        "GET /api/v1/taxis/popular-routes",
        "GET /api/v1/taxis/by-slug/{slug}",
        "GET /api/v1/taxis/{id:guid}",
        "POST /api/v1/payments/webhook",
        "POST /api/v1/payments/play/rtdn",
        "GET /api/v1/home/feed",
        "GET /api/v1/home/nearby-feed",
        "GET /api/v1/config/payment-feature",
        "GET /api/v1/config/app-tabs",
        "GET /api/v1/config/app-version",
        "GET /api/v1/config/map-discovery",
        "GET /api/v1/plans/catalog",
        "POST /api/v1/taxi-requests/offers/{offerId:guid}/ack",
    ];

    public static readonly string[] Seeker =
    [
        .. TripsCore, .. PlannerReads, .. ExecutionTravellerSide, .. Shortlist, .. Lifecycle, .. Sharing, .. Expenses, .. Payments,
        .. TaxiRequestOnTrip, .. TaxiRequest, .. Buddies, .. PersonSafety, .. Journeys, .. SeekerFeed, .. Social,
    ];
}
