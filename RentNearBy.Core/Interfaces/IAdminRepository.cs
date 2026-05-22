using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IAdminRepository : IRepository<Admin>
{
    Task<Admin?> GetByPhoneAsync(string phoneNumber);
}
