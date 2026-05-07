using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using RentNearBy.Core.Entities;

namespace RentNearBy.Infrastructure.Services;

public class JwtService(IConfiguration configuration) : IJwtService
{
    public string GenerateToken(User user, Guid sessionId)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(
            configuration["Jwt:Key"] ?? throw new InvalidOperationException("JWT Key not configured")));

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim("session_id", sessionId.ToString()),
            new Claim(ClaimTypes.MobilePhone, user.PhoneNumber),
            new Claim(ClaimTypes.Name, user.Name ?? string.Empty),
            new Claim("is_admin", user.IsAdmin.ToString().ToLower())
        };

        var token = new JwtSecurityToken(
            issuer: configuration["Jwt:Issuer"],
            audience: configuration["Jwt:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddDays(365),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256)
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
