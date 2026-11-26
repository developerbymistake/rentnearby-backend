using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;

namespace RentNearBy.Api.Extensions;

public static class AuthenticationExtensions
{
    public const string RevokeReasonItem = "rnb.session.revokeReason";

    // D-34 / FB-42: the 401 of a server-ended session, in the ApiResponse envelope with error.type = the reason.
    public static RentNearBy.Core.DTOs.Responses.ApiResponse<object> RevokedBody(string reason) => new()
    {
        Status = "401",
        Code = 401,
        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        Error = new RentNearBy.Core.DTOs.Responses.ApiError { Message = SessionRevokeReasons.MessageOf(reason), Type = reason },
    };

    public static IServiceCollection AddJwtAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var key = configuration["Jwt:Key"] ?? throw new InvalidOperationException("JWT Key not configured");
        var issuer = configuration["Jwt:Issuer"] ?? "RentNearBy";
        var audience = configuration["Jwt:Audience"] ?? "RentNearBy";

        services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        })
        .AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = issuer,
                ValidAudience = audience,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key))
            };

            options.Events = new JwtBearerEvents
            {
                // SignalR WebSocket connections cannot send HTTP headers —
                // read token from query string instead.
                OnMessageReceived = context =>
                {
                    var token = context.Request.Query["access_token"].FirstOrDefault();
                    if (!string.IsNullOrEmpty(token))
                        context.Token = token;
                    return Task.CompletedTask;
                },
                OnTokenValidated = async context =>
                {
                    var sessionIdClaim = context.Principal?.FindFirst("session_id")?.Value;
                    if (!Guid.TryParse(sessionIdClaim, out var sessionId))
                    {
                        context.Fail("Invalid session");
                        return;
                    }

                    var actorType = context.Principal?.FindFirst("actor_type")?.Value;
                    var unitOfWork = context.HttpContext.RequestServices.GetRequiredService<IUnitOfWork>();

                    if (actorType == "admin")
                    {
                        var adminSession = await unitOfWork.AdminSessions.GetActiveAdminSessionAsync(sessionId);
                        if (adminSession == null)
                            context.Fail("Session revoked or expired");
                    }
                    else
                    {
                        var roleId = await unitOfWork.Sessions.GetActiveSessionRoleAsync(sessionId);
                        if (roleId == null)
                        {
                            // FB-42: only on failure, one more read tells the app why (role changed, account paused).
                            if (await unitOfWork.Sessions.GetRevokeReasonAsync(sessionId) is { } reason && SessionRevokeReasons.IsKnown(reason))
                                context.HttpContext.Items[RevokeReasonItem] = reason;
                            context.Fail("Session revoked or expired");
                            return;
                        }
                        try
                        {
                            RoleClaimSync.Apply(context.Principal!, roleId.Value);
                        }
                        catch (InvalidOperationException)
                        {
                            context.Fail("Invalid role");
                        }
                    }
                },
                OnChallenge = async context =>
                {
                    if (context.HttpContext.Items[RevokeReasonItem] is not string reason) return;
                    context.HandleResponse();
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(RevokedBody(reason)));
                }
            };
        });

        services.AddAuthorization(ConfigurePolicies);
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, SeekerForbiddenResultHandler>();

        return services;
    }

    public static void ConfigurePolicies(AuthorizationOptions options)
    {
        options.AddPolicy("AdminOnly", policy =>
            policy.RequireClaim("actor_type", "admin"));
        // JwtBearer's inbound claim map turns the token's "role" claim into ClaimTypes.Role; OnTokenValidated keeps it live.
        options.AddPolicy("CanListStays", policy =>
            policy.RequireClaim(ClaimTypes.Role, UserRoles.StayOwner));
        options.AddPolicy("CanListTaxis", policy =>
            policy.RequireClaim(ClaimTypes.Role, UserRoles.TaxiDriver));
        options.AddPolicy(AuthPolicies.SeekerOnly, policy =>
            policy.RequireClaim(ClaimTypes.Role, UserRoles.Seeker));
    }
}
