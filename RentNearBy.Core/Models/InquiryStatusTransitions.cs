namespace RentNearBy.Core.Models;

// The first transition-table pattern in this codebase — Inquiry status previously had zero
// server-side sequencing enforcement (InquiryStatuses.cs is just flat constants). This makes the
// already-documented intended flow (Submitted -> Contacted -> Confirmed, with Cancelled/Rejected as
// terminal branches reachable from any non-terminal status — see inquiry_status.dart and both repos'
// CLAUDE.md) actually enforced. Self-loops are deliberate on every status so a note-only edit (same
// status, different Note) stays legal everywhere, including on terminal inquiries. Cancelled/Rejected
// both allow one escape hatch back to Contacted — without it, a mis-click would be permanently
// unrecoverable short of a raw DB edit, which today's zero-enforcement behavior lets an admin avoid.
public static class InquiryStatusTransitions
{
    private static readonly Dictionary<string, HashSet<string>> Allowed = new()
    {
        [InquiryStatuses.Submitted] = [InquiryStatuses.Submitted, InquiryStatuses.Contacted, InquiryStatuses.Cancelled, InquiryStatuses.Rejected],
        [InquiryStatuses.Contacted] = [InquiryStatuses.Contacted, InquiryStatuses.Confirmed, InquiryStatuses.Cancelled, InquiryStatuses.Rejected],
        [InquiryStatuses.Confirmed] = [InquiryStatuses.Confirmed, InquiryStatuses.Cancelled],
        [InquiryStatuses.Cancelled] = [InquiryStatuses.Cancelled, InquiryStatuses.Contacted],
        [InquiryStatuses.Rejected] = [InquiryStatuses.Rejected, InquiryStatuses.Contacted],
    };

    public static bool IsAllowed(string from, string to) => Allowed.TryGetValue(from, out var next) && next.Contains(to);
}
