using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

public static class AdminEndpoints
{
    public static RouteGroupBuilder MapAdminEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/districts", AdminHandlers.GetDistricts);
        group.MapPost("/districts", AdminHandlers.CreateDistrict).RequireAuthorization("AdminOnly");
        group.MapPut("/districts/{id:guid}/active", AdminHandlers.ToggleDistrictActive).RequireAuthorization("AdminOnly");
        group.MapPut("/districts/{id:guid}/force-active", AdminHandlers.ForceActivateDistrict).RequireAuthorization("AdminOnly");
        group.MapDelete("/districts/{id:guid}", AdminHandlers.DeleteDistrict).RequireAuthorization("AdminOnly");

        group.MapGet("/states", AdminHandlers.GetStates);
        group.MapPut("/states/{stateName}/active", AdminHandlers.BulkToggleStateActive).RequireAuthorization("AdminOnly");

        group.MapGet("/cities", AdminHandlers.GetCities);
        group.MapPost("/cities", AdminHandlers.CreateCity).RequireAuthorization("AdminOnly");
        group.MapDelete("/cities/{id:guid}", AdminHandlers.DeleteCity).RequireAuthorization("AdminOnly");

        group.MapGet("/room-types", AdminHandlers.GetRoomTypes);
        group.MapPost("/room-types", AdminHandlers.CreateRoomType).RequireAuthorization("AdminOnly");
        group.MapPut("/room-types/{id:guid}", AdminHandlers.UpdateRoomType).RequireAuthorization("AdminOnly");
        group.MapDelete("/room-types/{id:guid}", AdminHandlers.DeleteRoomType).RequireAuthorization("AdminOnly");

        group.MapGet("/plot-types", AdminHandlers.GetPlotTypes);
        group.MapPost("/plot-types", AdminHandlers.CreatePlotType).RequireAuthorization("AdminOnly");
        group.MapPut("/plot-types/{id:guid}", AdminHandlers.UpdatePlotType).RequireAuthorization("AdminOnly");
        group.MapDelete("/plot-types/{id:guid}", AdminHandlers.DeletePlotType).RequireAuthorization("AdminOnly");

        group.MapGet("/report-reasons", AdminHandlers.GetReportReasons);
        group.MapPost("/report-reasons", AdminHandlers.CreateReportReason).RequireAuthorization("AdminOnly");
        group.MapPut("/report-reasons/{id:guid}", AdminHandlers.UpdateReportReason).RequireAuthorization("AdminOnly");
        group.MapDelete("/report-reasons/{id:guid}", AdminHandlers.DeleteReportReason).RequireAuthorization("AdminOnly");

        group.MapGet("/stats", AdminHandlers.GetStats).RequireAuthorization("AdminOnly");

        group.MapGet("/geocode", AdminHandlers.Geocode).RequireAuthorization("AdminOnly");

        group.MapGet("/users", AdminHandlers.GetUsers).RequireAuthorization("AdminOnly");
        group.MapGet("/deleted-accounts", AdminHandlers.GetDeletedAccounts).RequireAuthorization("AdminOnly");
        group.MapGet("/users/{id:guid}", AdminHandlers.GetUserById).RequireAuthorization("AdminOnly");
        group.MapPut("/users/{id:guid}/status", AdminHandlers.UpdateUserStatus).RequireAuthorization("AdminOnly");
        group.MapGet("/users/{id:guid}/plans", AdminUserPlanHandlers.GetUserPlans).RequireAuthorization("AdminOnly");
        group.MapPost("/users/{id:guid}/plan/grant", AdminUserPlanHandlers.GrantPlan).RequireAuthorization("AdminOnly");
        group.MapPost("/users/{id:guid}/plan/revoke", AdminUserPlanHandlers.RevokePlan).RequireAuthorization("AdminOnly");
        group.MapPut("/users/{id:guid}/role", AdminRoleHandlers.UpdateUserRole).RequireAuthorization("AdminOnly");

        group.MapGet("/role-requests", AdminRoleHandlers.GetRoleRequests).RequireAuthorization("AdminOnly");
        group.MapPut("/role-requests/{id:guid}/approve", AdminRoleHandlers.ApproveRoleRequest).RequireAuthorization("AdminOnly");
        group.MapPut("/role-requests/{id:guid}/reject", AdminRoleHandlers.RejectRoleRequest).RequireAuthorization("AdminOnly");

        group.MapGet("/purchases", AdminPurchaseHandlers.GetPurchases).RequireAuthorization("AdminOnly");
        group.MapGet("/purchases/{id:guid}", AdminPurchaseHandlers.GetPurchaseById).RequireAuthorization("AdminOnly");
        group.MapPost("/purchases/{id:guid}/recheck", AdminPurchaseHandlers.RecheckPurchase).RequireAuthorization("AdminOnly");
        group.MapPost("/purchases/{id:guid}/refund", AdminPurchaseHandlers.RefundPurchase).RequireAuthorization("AdminOnly");
        group.MapGet("/purchases/{id:guid}/play-token", AdminPurchaseHandlers.GetPlayToken).RequireAuthorization("AdminOnly");

        group.MapGet("/payment-feature", AdminHandlers.GetPaymentFeature).RequireAuthorization("AdminOnly");
        group.MapPut("/payment-feature", AdminHandlers.UpdatePaymentFeature).RequireAuthorization("AdminOnly");

        group.MapGet("/app-tabs", AdminHandlers.GetAppTabs).RequireAuthorization("AdminOnly");
        group.MapPut("/app-tabs/{tabKey}", AdminHandlers.UpdateAppTab).RequireAuthorization("AdminOnly");

        group.MapGet("/app-version", AdminHandlers.GetAppVersionConfigs).RequireAuthorization("AdminOnly");
        group.MapPut("/app-version/{platform}", AdminHandlers.UpdateAppVersionConfig).RequireAuthorization("AdminOnly");

        group.MapGet("/coupons", AdminCouponHandlers.GetCoupons).RequireAuthorization("AdminOnly");
        group.MapGet("/coupons/{id:guid}", AdminCouponHandlers.GetCouponById).RequireAuthorization("AdminOnly");
        group.MapPost("/coupons", AdminCouponHandlers.CreateCoupon).RequireAuthorization("AdminOnly");
        group.MapPut("/coupons/{id:guid}", AdminCouponHandlers.UpdateCoupon).RequireAuthorization("AdminOnly");
        group.MapPost("/coupons/{id:guid}/revoke", AdminCouponHandlers.RevokeCoupon).RequireAuthorization("AdminOnly");
        group.MapGet("/coupons/{id:guid}/redemptions", AdminCouponHandlers.GetRedemptions).RequireAuthorization("AdminOnly");

        group.MapGet("/plans", AdminPlanHandlers.GetPlans).RequireAuthorization("AdminOnly");
        group.MapPost("/plans", AdminPlanHandlers.CreatePlan).RequireAuthorization("AdminOnly");
        group.MapPut("/plans/{id:guid}", AdminPlanHandlers.UpdatePlan).RequireAuthorization("AdminOnly");

        group.MapGet("/listings", AdminHandlers.GetAdminListings).RequireAuthorization("AdminOnly");
        group.MapGet("/listings/{id:guid}", AdminHandlers.GetAdminListingById).RequireAuthorization("AdminOnly");
        group.MapPut("/listings/{id:guid}/status", AdminHandlers.ToggleAdminListingStatus).RequireAuthorization("AdminOnly");
        group.MapDelete("/listings/{id:guid}", AdminHandlers.DeleteAdminListing).RequireAuthorization("AdminOnly");

        group.MapGet("/reports", AdminHandlers.GetReports).RequireAuthorization("AdminOnly");
        group.MapGet("/reports/{id:guid}", AdminHandlers.GetReportById).RequireAuthorization("AdminOnly");
        group.MapPut("/reports/{id:guid}/resolve", AdminHandlers.ResolveReport).RequireAuthorization("AdminOnly");
        // Atomic report-driven takedown — replaces the old "Deactivate Post" two-call sequence
        // (status toggle + resolve) with one transactional endpoint. Single route: report.ListingType
        // (Room/Plot) is dispatched on internally, see AdminHandlers.RejectReport.
        group.MapPut("/reports/{id:guid}/reject", AdminHandlers.RejectReport).RequireAuthorization("AdminOnly");

        // Separate Room/Plot pairs, not unified — see the plan comment above ToGoLiveRequestDto in
        // AdminHandlers/PlotListingHandlers for why a unified feed isn't worth it here.
        group.MapGet("/room-golive-requests", AdminHandlers.GetRoomGoLiveRequests).RequireAuthorization("AdminOnly");
        group.MapGet("/room-golive-requests/{id:guid}", AdminHandlers.GetRoomGoLiveRequestById).RequireAuthorization("AdminOnly");
        group.MapPut("/room-golive-requests/{id:guid}/approve", AdminHandlers.ApproveRoomGoLiveRequest).RequireAuthorization("AdminOnly");
        group.MapPut("/room-golive-requests/{id:guid}/reject", AdminHandlers.RejectRoomGoLiveRequest).RequireAuthorization("AdminOnly");

        group.MapGet("/plot-golive-requests", PlotListingHandlers.GetPlotGoLiveRequests).RequireAuthorization("AdminOnly");
        group.MapGet("/plot-golive-requests/{id:guid}", PlotListingHandlers.GetPlotGoLiveRequestById).RequireAuthorization("AdminOnly");
        group.MapPut("/plot-golive-requests/{id:guid}/approve", PlotListingHandlers.ApprovePlotGoLiveRequest).RequireAuthorization("AdminOnly");
        group.MapPut("/plot-golive-requests/{id:guid}/reject", PlotListingHandlers.RejectPlotGoLiveRequest).RequireAuthorization("AdminOnly");

        group.MapPost("/broadcast-notification", BroadcastHandlers.SendBroadcast).RequireAuthorization("AdminOnly");

        // Same handler the consumer app calls under /api/v1/chat/question-templates —
        // mirrors how GetDistricts is mapped under both /admin/districts and
        // /listings/locations/districts. Deactivate-not-delete: see ToggleQuestionTemplateActive.
        group.MapGet("/question-templates", ChatHandlers.GetQuestionTemplates);
        group.MapPost("/question-templates", ChatHandlers.CreateQuestionTemplate).RequireAuthorization("AdminOnly");
        group.MapPut("/question-templates/{id:guid}", ChatHandlers.UpdateQuestionTemplate).RequireAuthorization("AdminOnly");
        group.MapPut("/question-templates/{id:guid}/active", ChatHandlers.ToggleQuestionTemplateActive).RequireAuthorization("AdminOnly");

        return group;
    }
}
