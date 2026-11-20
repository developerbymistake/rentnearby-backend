namespace RentNearBy.Core.Models;

// Codes of the 409 AccountHasActiveItems answer of DELETE /account, in the order they are reported.
public static class AccountDeletionReasons
{
    public const string ActiveTaxiBooking = "ActiveTaxiBooking";
    public const string ActiveStayBooking = "ActiveStayBooking";
    public const string SelectedTaxiOffer = "SelectedTaxiOffer";
    public const string OpenTaxiRequest = "OpenTaxiRequest";
    public const string OpenStayEnquiry = "OpenStayEnquiry";
    public const string LiveListing = "LiveListing";
    // Reported by the buddy money participant (IAccountDeletionParticipant), not by Blocks(); {0} = trip title.
    public const string SharedTripOpenBalance = "SharedTripOpenBalance";
    public const string SharedTripOpenBalanceMessage = "You have money to settle on {0}.";

    public static readonly IReadOnlyList<string> All =
        [ActiveTaxiBooking, ActiveStayBooking, SelectedTaxiOffer, OpenTaxiRequest, OpenStayEnquiry, LiveListing];

    public static readonly IReadOnlyDictionary<string, string> Messages = new Dictionary<string, string>
    {
        [ActiveTaxiBooking] = "You have an active taxi booking. You can delete your account after it ends.",
        [ActiveStayBooking] = "You have an active stay booking. You can delete your account after it ends.",
        [SelectedTaxiOffer] = "A traveller has chosen you. Confirm or decline the trip first.",
        [OpenTaxiRequest] = "You have an open taxi request. Cancel it first.",
        [OpenStayEnquiry] = "You have an open stay enquiry. Close it first.",
        [LiveListing] = "Take your listing offline first.",
    };
}

public sealed record AccountActivity(
    bool ActiveTaxiBooking, bool ActiveStayBooking, bool SelectedTaxiOffer,
    bool OpenTaxiRequest, bool OpenStayEnquiry, bool LiveListing);

public sealed record AccountDeletionBlock(string Code, string Message);

public sealed record AccountDeletionResult(IReadOnlyList<AccountDeletionBlock> Reasons)
{
    public static readonly AccountDeletionResult Done = new([]);

    public bool Deleted => Reasons.Count == 0;
}

public static class AccountDeletionRules
{
    public const string ErrorType = "AccountHasActiveItems";
    public const string DeletedUserName = "Deleted user";

    public static IReadOnlyList<AccountDeletionBlock> Blocks(AccountActivity a)
    {
        var flags = new[]
        {
            a.ActiveTaxiBooking, a.ActiveStayBooking, a.SelectedTaxiOffer,
            a.OpenTaxiRequest, a.OpenStayEnquiry, a.LiveListing,
        };
        var blocks = new List<AccountDeletionBlock>();
        for (var i = 0; i < flags.Length; i++)
        {
            if (!flags[i]) continue;
            var code = AccountDeletionReasons.All[i];
            blocks.Add(new AccountDeletionBlock(code, AccountDeletionReasons.Messages[code]));
        }
        return blocks;
    }
}
