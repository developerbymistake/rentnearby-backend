using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

public static class PaymentEndpoints
{
    public static RouteGroupBuilder MapPaymentEndpoints(this RouteGroupBuilder group)
    {
        // /cancel-order (room/plot listing payments) removed with the old membership flow — credit-pack
        // purchases have their own /api/v1/credit-packs/cancel-order now.
        // Razorpay calls this directly, server-to-server — no user JWT, authenticated instead
        // by its own HMAC signature header (verified inside the handler).
        group.MapPost("/webhook", PaymentHandlers.RazorpayWebhook).AllowAnonymous();
        return group;
    }
}
