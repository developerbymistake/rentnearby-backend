namespace RentNearBy.Core.DTOs.Requests;

// Full-replace, not a diff. An empty list is a legitimate "unassign from all services" request.
public record SetAgentServicesRequest(List<Guid> ServiceIds);
