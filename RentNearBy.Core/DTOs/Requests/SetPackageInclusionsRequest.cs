namespace RentNearBy.Core.DTOs.Requests;

// Full-replace, not a diff — handler does RemoveRange + AddRange in one SaveChangesAsync, exact mirror
// of how BannerHandlers manipulates db.BannerDismissals directly. An empty list is a legitimate
// "clear all inclusions" request, not an error.
public record SetPackageInclusionsRequest(List<Guid> InclusionIds);
