using RentNearBy.Api.Filters;
using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

// Trip bookings under /api/v1/trips (the group already carries TripPlannerGateFilter). Not tab-gated:
// enquiry create checks the Stays/Taxi tab itself; owner and driver actions check ownership, not the policy.
public static class TripExecutionEndpoints
{
    public static RouteGroupBuilder MapTripExecutionEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/{tripId:guid}/enquiries", TripEnquiryHandlers.Create).Gated();

        // Guided-flow shortlist (server-owned, max 3 per step) and the phone-booking close.
        group.MapGet("/{tripId:guid}/shortlist", TripShortlistHandlers.Get).RequireAuthorization();
        group.MapPut("/{tripId:guid}/steps/{stepKey}/shortlist/{listingId:guid}", TripShortlistHandlers.Add).RequireAuthorization();
        group.MapDelete("/{tripId:guid}/steps/{stepKey}/shortlist/{listingId:guid}", TripShortlistHandlers.Remove).RequireAuthorization();
        group.MapPost("/{tripId:guid}/steps/{stepKey}/shortlist/close", TripShortlistHandlers.Close).RequireAuthorization();
        group.MapPost("/{tripId:guid}/steps/{stepKey}/mark-booked", TripShortlistHandlers.MarkBooked).RequireAuthorization();

        group.MapGet("/enquiries/owner", TripOwnerHandlers.GetMine).Gated("CanListStays").WithMetadata(new SkipTripPlannerGateAttribute());
        // Get one, withdraw and cancel serve journey leads too (no planner gate, party checked in the handlers).
        group.MapGet("/enquiries/by-conversation/{conversationId:guid}", TripEnquiryHandlers.GetByConversation).Gated().WithMetadata(new SkipTripPlannerGateAttribute());
        group.MapGet("/enquiries/{id:guid}", TripEnquiryHandlers.GetOne).Gated().WithMetadata(new SkipTripPlannerGateAttribute());
        group.MapPost("/enquiries/{id:guid}/withdraw", TripEnquiryHandlers.Withdraw).Gated().WithMetadata(new SkipTripPlannerGateAttribute());
        group.MapPost("/enquiries/{id:guid}/refresh-context", TripEnquiryHandlers.RefreshContext).Gated();
        group.MapPost("/enquiries/{id:guid}/lead-update", TripEnquiryHandlers.UpdateLead).Gated();
        group.MapPost("/enquiries/{id:guid}/cancel", TripEnquiryHandlers.Cancel).Gated().WithMetadata(new SkipTripPlannerGateAttribute());
        group.MapPost("/enquiries/{id:guid}/offers/{offerId:guid}/accept", TripEnquiryHandlers.AcceptOffer).Gated();
        group.MapPost("/enquiries/{id:guid}/offers/{offerId:guid}/decline", TripEnquiryHandlers.DeclineOffer).Gated();

        // Host side answers open enquiries whatever the planner flag says; only new asks stay planner-gated.
        group.MapPost("/enquiries/{id:guid}/offers", TripOwnerHandlers.SendOffer).Gated().WithMetadata(new SkipTripPlannerGateAttribute());
        group.MapPost("/enquiries/{id:guid}/not-available", TripOwnerHandlers.NotAvailable).Gated().WithMetadata(new SkipTripPlannerGateAttribute());

        // Counter and change request: guest side. Revised offer and accept change: host side (checked in the handlers).
        group.MapPost("/enquiries/{id:guid}/counter", TripEnquiryHandlers.Counter).Gated();
        group.MapPost("/enquiries/{id:guid}/change-request", TripEnquiryHandlers.ChangeRequest).Gated();
        group.MapPost("/enquiries/{id:guid}/revised-offer", TripOwnerHandlers.RevisedOffer).Gated().WithMetadata(new SkipTripPlannerGateAttribute());
        group.MapPost("/enquiries/{id:guid}/change-request/{offerId:guid}/accept", TripOwnerHandlers.AcceptChange).Gated().WithMetadata(new SkipTripPlannerGateAttribute());

        // Trip lifecycle. complete / mute / summary have no booking side effect, so they skip the execution gate.
        group.MapPost("/{tripId:guid}/problems", TripLifecycleHandlers.Problem).Gated();
        group.MapPost("/{tripId:guid}/complete", TripLifecycleHandlers.Complete).RequireAuthorization();
        group.MapPut("/{tripId:guid}/mute", TripLifecycleHandlers.Mute).RequireAuthorization();
        group.MapGet("/{tripId:guid}/summary", TripLifecycleHandlers.Summary).RequireAuthorization();
        group.MapPost("/enquiries/{id:guid}/report-traveller", TripLifecycleHandlers.ReportTraveller).Gated().WithMetadata(new SkipTripPlannerGateAttribute());

        // Stay advance: ask / received / not-yet are host-only, proof-sent is guest-only (checked in the handlers).
        group.MapPost("/enquiries/{id:guid}/advance/ask", TripAdvanceHandlers.Ask).Gated().WithMetadata(new SkipTripPlannerGateAttribute());
        group.MapPost("/enquiries/{id:guid}/advance/proof-sent", TripAdvanceHandlers.ProofSent).Gated();
        group.MapPost("/enquiries/{id:guid}/advance/received", TripAdvanceHandlers.Received).Gated().WithMetadata(new SkipTripPlannerGateAttribute());
        group.MapPost("/enquiries/{id:guid}/advance/not-yet", TripAdvanceHandlers.NotYet).Gated().WithMetadata(new SkipTripPlannerGateAttribute());

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
