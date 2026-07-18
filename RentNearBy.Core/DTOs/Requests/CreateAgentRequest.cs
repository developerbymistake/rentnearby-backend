namespace RentNearBy.Core.DTOs.Requests;

// Phone/WhatsAppNumber deliberately separate (consumer app renders two distinct Call/WhatsApp buttons).
// Photo is uploaded separately via PhotoService.SaveAgentPhotoAsync, never as part of this JSON body.
// Category assignment is a separate full-replace call (SetAgentCategoriesRequest), not part of create.
public record CreateAgentRequest(string Name, string Phone, string WhatsAppNumber);
