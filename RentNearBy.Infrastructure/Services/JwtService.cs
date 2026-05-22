using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using RentNearBy.Core.Entities;

namespace RentNearBy.Infrastructure.Services;

public class JwtService(IConfiguration configuration) : IJwtService
{
    private SymmetricSecurityKey Key => new(Encoding.UTF8.GetBytes(
        configuration["Jwt:Key"] ?? throw new InvalidOperationException("JWT Key not configured")));

    public string GenerateToken(User user, Guid sessionId)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim("session_id", sessionId.ToString()),
            new Claim(ClaimTypes.MobilePhone, user.PhoneNumber),
            new Claim(ClaimTypes.Name, user.Name ?? string.Empty),
            new Claim("actor_type", "user"),
        };

        return BuildToken(claims);
    }

    public string GenerateAdminToken(Admin admin, Guid sessionId)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, admin.Id.ToString()),
            new Claim("session_id", sessionId.ToString()),
            new Claim(ClaimTypes.MobilePhone, admin.PhoneNumber),
            new Claim(ClaimTypes.Name, admin.Name ?? string.Empty),
            new Claim("actor_type", "admin"),
        };

        return BuildToken(claims);
    }

    private string BuildToken(Claim[] claims)
    {
        var token = new JwtSecurityToken(
            issuer: configuration["Jwt:Issuer"],
            audience: configuration["Jwt:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddDays(365),
            signingCredentials: new SigningCredentials(Key, SecurityAlgorithms.HmacSha256)
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
