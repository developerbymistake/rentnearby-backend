namespace RentNearBy.Core.Models;

// The Google Play "manage subscription" link of myPlan.manageUrl and of the account-deletion block (one builder for both).
public static class PlayManageUrls
{
    public static string For(string productId, string? packageName)
    {
        var url = $"https://play.google.com/store/account/subscriptions?sku={Uri.EscapeDataString(productId)}";
        return string.IsNullOrEmpty(packageName) ? url : $"{url}&package={Uri.EscapeDataString(packageName)}";
    }
}
