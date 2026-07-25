namespace RentNearBy.Core.Models;

// The first transition-table pattern in this codebase — Inquiry status previously had zero
// server-side sequencing enforcement (InquiryStatuses.cs is just flat constants). This makes the
// already-documented intended flow (Submitted -> Contacted -> Closed) actually enforced. The
// business model is pure lead-generation/middleware, so the platform doesn't track fine-grained
// post-contact outcomes — everything past "agent engaged" collapses to one terminal Closed state,
// settable by either the agent or an admin. Closed is NOT reachable directly from Submitted — an
// agent must at least mark Contacted first. Self-loops are deliberate on every status so a
// note-only edit (same status, different Note) stays legal everywhere, including on terminal
// inquiries. Closed allows one escape hatch back to Contacted — without it, a mis-click would be
// permanently unrecoverable short of a raw DB edit, which today's zero-enforcement behavior lets an
// admin avoid.
public static class InquiryStatusTransitions
{
    private static readonly Dictionary<string, HashSet<string>> Allowed = new()
    {
        [InquiryStatuses.Submitted] = [InquiryStatuses.Submitted, InquiryStatuses.Contacted],
        [InquiryStatuses.Contacted] = [InquiryStatuses.Contacted, InquiryStatuses.Closed],
        [InquiryStatuses.Closed] = [InquiryStatuses.Closed, InquiryStatuses.Contacted],
    };

    public static bool IsAllowed(string from, string to) => Allowed.TryGetValue(from, out var next) && next.Contains(to);
}
