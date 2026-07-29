using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

// Public (unauthenticated) website enquiry flow, mounted at "/api/v1/web-enquiry" — entirely separate
// route group from "/api/v1/enquiries" (EnquiryEndpoints), so nothing here can affect the existing
// consumer-app enquiry surface. See WebEnquiryHandlers' own doc comment for the 3-step flow.
public static class WebEnquiryEndpoints
{
    public static RouteGroupBuilder MapWebEnquiryEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/start", WebEnquiryHandlers.Start).AllowAnonymous();
        group.MapPost("/confirm", WebEnquiryHandlers.Confirm).AllowAnonymous();
        group.MapPost("/verify-otp", WebEnquiryHandlers.VerifyOtp).AllowAnonymous();

        return group;
    }
}
