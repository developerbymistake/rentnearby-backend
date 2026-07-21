namespace RentNearBy.Core.DTOs.Requests;

// Phone/WhatsAppNumber deliberately separate (consumer app renders two distinct Call/WhatsApp buttons).
// Photo is uploaded separately via PhotoService.SaveAgentPhotoAsync, never as part of this JSON body.
// Category assignment is a separate full-replace call (SetAgentCategoriesRequest), not part of create.
// UserId is required and immutable — an Agent is a role on an existing consumer account, never a
// separate identity, and is never re-linked after creation (delete and recreate instead, matching
// ServiceCategory.ServiceSectionId's convention).
public record CreateAgentRequest(string Name, string Phone, string WhatsAppNumber, Guid UserId, int? Experience);
