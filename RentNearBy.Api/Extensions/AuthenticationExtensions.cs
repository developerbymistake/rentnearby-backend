using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using RentNearBy.Core.Interfaces;

namespace RentNearBy.Api.Extensions;

public static class AuthenticationExtensions
{
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
                        var session = await unitOfWork.Sessions.GetActiveSessionAsync(sessionId);
                        if (session == null)
                            context.Fail("Session revoked or expired");
                    }
                }
            };
        });

        services.AddAuthorization(options =>
        {
            options.AddPolicy("AdminOnly", policy =>
                policy.RequireClaim("actor_type", "admin"));
        });

        return services;
    }
}
