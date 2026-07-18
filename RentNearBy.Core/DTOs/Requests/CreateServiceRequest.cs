namespace RentNearBy.Core.DTOs.Requests;

// Cover photo is uploaded separately via the dedicated photo endpoint (PhotoService.SaveServiceCoverPhotoAsync),
// never as part of this JSON body — matches the codebase's multipart-only-for-files convention.
public record CreateServiceRequest(
    Guid ServiceCategoryId, string Name, string IconName, string ShortDescription, string FullDescription,
    int SortOrder = 999, bool IsFeatured = false);
