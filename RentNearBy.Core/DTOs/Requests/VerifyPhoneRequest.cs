namespace RentNearBy.Core.DTOs.Requests;

public record VerifyPhoneRequest(string PhoneNumber, string Otp);
