using RentNearBy.Api.Filters;
using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

// Trip bookings under /api/v1/trips (the group already carries TripPlannerGateFilter). Not tab-gated:
// enquiry create checks the Stays/Taxi tab itself; owner and driver actions check ownership, not the policy.
public static class TripExecutionEndpoints
{
    public static RouteGroupBuilder MapTripExecutionEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/{tripId:guid}/enquiries/ask-again", TripEnquiryHandlers.AskAgain).Gated();
        group.MapPost("/{tripId:guid}/enquiries", TripEnquiryHandlers.Create).Gated();
        group.MapPost("/{tripId:guid}/phone-bookings", TripEnquiryHandlers.CreatePhoneBooking).Gated();

        group.MapGet("/enquiries/owner", TripOwnerHandlers.GetMine).Gated("CanListStays");
        group.MapGet("/enquiries/by-conversation/{conversationId:guid}", TripEnquiryHandlers.GetByConversation).Gated();
        group.MapGet("/enquiries/{id:guid}", TripEnquiryHandlers.GetOne).Gated();
        group.MapPut("/enquiries/{id:guid}/phone-booking", TripEnquiryHandlers.EditPhoneBooking).Gated();
        group.MapPost("/enquiries/{id:guid}/host-confirm-link/resend", TripEnquiryHandlers.ResendHostLink).Gated();
        group.MapPost("/enquiries/{id:guid}/withdraw", TripEnquiryHandlers.Withdraw).Gated();
        group.MapPost("/enquiries/{id:guid}/cancel", TripEnquiryHandlers.Cancel).Gated();
        group.MapPost("/enquiries/{id:guid}/offers/{offerId:guid}/accept", TripEnquiryHandlers.AcceptOffer).Gated();
        group.MapPost("/enquiries/{id:guid}/offers/{offerId:guid}/decline", TripEnquiryHandlers.DeclineOffer).Gated();

        group.MapPost("/enquiries/{id:guid}/offers", TripOwnerHandlers.SendOffer).Gated();
        group.MapPost("/enquiries/{id:guid}/not-available", TripOwnerHandlers.NotAvailable).Gated();
        group.MapPost("/enquiries/{id:guid}/host-confirm", TripOwnerHandlers.HostConfirm).Gated();

        // Counter and change request: guest side. Revised offer and accept change: host side (checked in the handlers).
        group.MapPost("/enquiries/{id:guid}/counter", TripEnquiryHandlers.Counter).Gated();
        group.MapPost("/enquiries/{id:guid}/change-request", TripEnquiryHandlers.ChangeRequest).Gated();
        group.MapPost("/enquiries/{id:guid}/revised-offer", TripOwnerHandlers.RevisedOffer).Gated();
        group.MapPost("/enquiries/{id:guid}/change-request/{offerId:guid}/accept", TripOwnerHandlers.AcceptChange).Gated();

        // Trip lifecycle. complete / mute / summary have no booking side effect, so they skip the execution gate.
        group.MapPost("/{tripId:guid}/problems", TripLifecycleHandlers.Problem).Gated();
        group.MapPost("/{tripId:guid}/complete", TripLifecycleHandlers.Complete).RequireAuthorization();
        group.MapPut("/{tripId:guid}/mute", TripLifecycleHandlers.Mute).RequireAuthorization();
        group.MapGet("/{tripId:guid}/summary", TripLifecycleHandlers.Summary).RequireAuthorization();
        group.MapPost("/enquiries/{id:guid}/report-traveller", TripLifecycleHandlers.ReportTraveller).Gated();

        // Stay advance: ask / received / not-yet are host-only, proof-sent is guest-only (checked in the handlers).
        group.MapPost("/enquiries/{id:guid}/advance/ask", TripAdvanceHandlers.Ask).Gated();
        group.MapPost("/enquiries/{id:guid}/advance/proof-sent", TripAdvanceHandlers.ProofSent).Gated();
        group.MapPost("/enquiries/{id:guid}/advance/received", TripAdvanceHandlers.Received).Gated();
        group.MapPost("/enquiries/{id:guid}/advance/not-yet", TripAdvanceHandlers.NotYet).Gated();

        // Taxi: "I booked" by the traveller; the driver's Trip leads list and actions.
        group.MapPost("/{tripId:guid}/taxi-bookings", TripEnquiryHandlers.CreateTaxiBooking).Gated();
        // Journey leads come from /taxis/ways/ask, which has no planner gate: the driver side answers whatever the planner flag says.
        group.MapGet("/leads/driver", TripLeadHandlers.GetMine).Gated("CanListTaxis").WithMetadata(new SkipTripPlannerGateAttribute());
        group.MapPost("/leads/{id:guid}/fare", TripLeadHandlers.Fare).Gated().WithMetadata(new SkipTripPlannerGateAttribute());
        group.MapPost("/leads/{id:guid}/not-available", TripLeadHandlers.NotAvailable).Gated().WithMetadata(new SkipTripPlannerGateAttribute());
        group.MapPost("/leads/{id:guid}/confirm", TripLeadHandlers.Confirm).Gated().WithMetadata(new SkipTripPlannerGateAttribute());
        group.MapPost("/leads/{id:guid}/deny", TripLeadHandlers.Deny).Gated().WithMetadata(new SkipTripPlannerGateAttribute());

        return group;
    }

    private static RouteHandlerBuilder Gated(this RouteHandlerBuilder route, string? policy = null)
    {
        var authorized = policy == null ? route.RequireAuthorization() : route.RequireAuthorization(policy);
        return authorized.AddEndpointFilter<TripExecutionGateFilter>();
    }
}
