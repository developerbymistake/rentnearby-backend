namespace RentNearBy.Core.Models;

// FB-33: what GET /me needs about the owner's listing rows of one kind, from ONE projected statement (a user has one live row and
// a short soft-deleted history). Status = the live row's status (null = none live).
public sealed record OwnerListingFacts(string? Status, bool SubmittedHistory, bool EverApproved, bool HasDeleted)
{
    public sealed record Row(string Status, bool IsDeleted, DateTime? SubmittedAt, bool EverApproved);

    public static OwnerListingFacts Of(IReadOnlyList<Row> rows, string draftStatus)
    {
        var submitted = rows.Any(r => r.Status != draftStatus || r.SubmittedAt != null || r.EverApproved);
        return new OwnerListingFacts(
            rows.FirstOrDefault(r => !r.IsDeleted)?.Status,
            submitted,
            submitted && rows.Any(r => r.EverApproved),
            rows.Any(r => r.IsDeleted));
    }
}
