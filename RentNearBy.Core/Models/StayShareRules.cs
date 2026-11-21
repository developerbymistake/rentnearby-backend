using System.Text.RegularExpressions;

namespace RentNearBy.Core.Models;

// Stay owner's UPI ID and WhatsApp number, shared in chat only when the owner chooses. No amount, no status.
public static class StayShareRules
{
    public const int UpiMax = 60, UpiMin = 5;
    // Shape only; the id is never resolved or checked against a bank.
    public const string UpiPattern = @"^[A-Za-z0-9._-]{2,50}@[A-Za-z]{2,64}$";
    private static readonly Regex Upi = new(UpiPattern, RegexOptions.Compiled);

    public const string MessageType = "stay_contact_share";
    public const string FallbackText = "UPI and WhatsApp shared. Update the app to see them.";
    public const string Preview = "UPI and WhatsApp shared";
    public const string NotSetError = "ShareDetailsNotSet";

    public static bool IsValidUpi(string? s)
    {
        if (s == null) return false;
        var v = s.Trim();
        return v.Length >= UpiMin && v.Length <= UpiMax && Upi.IsMatch(v);
    }

    // The card payload, written by the server from the listing only; null fields are left out.
    public static Dictionary<string, string>? Payload(string? stayName, string? upiId, string? whatsApp)
    {
        var upi = string.IsNullOrWhiteSpace(upiId) ? null : upiId.Trim();
        var wa = string.IsNullOrWhiteSpace(whatsApp) ? null : whatsApp.Trim();
        if (upi == null && wa == null) return null;
        var p = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(stayName)) p["stayName"] = stayName.Trim();
        if (upi != null) p["upiId"] = upi;
        if (wa != null) p["whatsApp"] = wa;
        p["text"] = FallbackText;
        return p;
    }
}
