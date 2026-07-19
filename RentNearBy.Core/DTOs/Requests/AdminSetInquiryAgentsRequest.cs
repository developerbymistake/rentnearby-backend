namespace RentNearBy.Core.DTOs.Requests;

// Full-set-replace — an empty list clears all assignments (equivalent to the old "unassign"), a
// non-empty list becomes the complete new set of assigned agents. Not add/remove verbs — mirrors
// SetAgentCategoriesRequest's exact shape for the same reason.
public record AdminSetInquiryAgentsRequest(List<Guid> AgentIds);
