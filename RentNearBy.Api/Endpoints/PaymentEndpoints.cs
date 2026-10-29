using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

public static class PaymentEndpoints
{
    public static RouteGroupBuilder MapPaymentEndpoints(this RouteGroupBuilder group)
    {
        // Razorpay calls this directly, server-to-server — no user JWT, authenticated instead
        // by its own HMAC signature header (verified inside the handler).
        group.MapPost("/webhook", PaymentHandlers.RazorpayWebhook).AllowAnonymous();
        return group;
    }
}
