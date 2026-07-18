namespace RentNearBy.Core.DTOs.Requests;

// AgentId is nullable — admin can unassign (remove) an agent at any time, not just assign. Assigning
// an agent while the Inquiry is Submitted auto-flips it to Contacted as one combined write (handler
// concern, not this DTO).
public record AdminAssignAgentRequest(Guid? AgentId);
