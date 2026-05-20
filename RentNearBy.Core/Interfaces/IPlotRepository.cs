using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IPlotRepository : IRepository<Plot>
{
    Task<IEnumerable<NearbyPlotDto>> GetNearbyAsync(double latitude, double longitude, double radiusKm, Guid cityId);
    Task<IEnumerable<Plot>> GetByUserIdAsync(Guid userId);
    Task<(IReadOnlyList<Plot> Items, bool HasMore)> GetByUserIdPagedAsync(Guid userId, int page, int pageSize);
    Task<Plot?> GetByIdWithPhotosAsync(Guid id);
    Task<(IReadOnlyList<Plot> Items, bool HasMore)> GetAllAsync(
        int page, int pageSize,
        string? plotType = null,
        bool? isActive = null,
        Guid? districtId = null,
        Guid? cityId = null);
    Task<IEnumerable<Plot>> GetActiveByUserIdAsync(Guid userId);
    Task AddPhotoAsync(PlotPhoto photo);
    void RemovePhoto(PlotPhoto photo);
}
