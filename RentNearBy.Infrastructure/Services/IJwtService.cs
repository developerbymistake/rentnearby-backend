using RentNearBy.Core.Entities;

namespace RentNearBy.Infrastructure.Services;

public interface IJwtService
{
    string GenerateToken(User user, Guid sessionId);
    string GenerateAdminToken(Admin admin, Guid sessionId);
}
