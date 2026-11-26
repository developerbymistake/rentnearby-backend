namespace RentNearBy.Core.Models.Buddy;

// Contract appendix A: type -> HTTP status and default message. The app switches on the type; messages are fallbacks.
public static class BuddyErrorTypes
{
    public const string SelfRequest = "SelfRequest";
    public const string PersonNotFound = "PersonNotFound";
    public const string AlreadyBuddies = "AlreadyBuddies";
    public const string TryLater = "TryLater";
    public const string WaitingLimit = "WaitingLimit";
    public const string BuddyLimit = "BuddyLimit";
    public const string CannotAddNow = "CannotAddNow";
    public const string BuddyRequestsOnHold = "BuddyRequestsOnHold";
    public const string RequestEnded = "RequestEnded";
    public const string CannotBlockSelf = "CannotBlockSelf";
    public const string CannotReportSelf = "CannotReportSelf";
    public const string TooManyBlocks = "TooManyBlocks";
    public const string InvalidReason = "InvalidReason";
    public const string TripNotFound = "TripNotFound";
    public const string TripFull = "TripFull";
    public const string TripLimitReached = "TripLimitReached";
    public const string TripCompleted = "TripCompleted";
    public const string NoPlaces = "NoPlaces";
    public const string InviteEnded = "InviteEnded";
    public const string NotOnTrip = "NotOnTrip";
    public const string TripHasOpenBalances = "TripHasOpenBalances";
    public const string NotTripOwner = "NotTripOwner";
    public const string ExpenseNotFound = "ExpenseNotFound";
    public const string InvalidAmount = "InvalidAmount";
    public const string TitleRequired = "TitleRequired";
    public const string InvalidCategory = "InvalidCategory";
    public const string InvalidDay = "InvalidDay";
    public const string NoSharers = "NoSharers";
    public const string NoteTooLong = "NoteTooLong";
    public const string ExpenseVersionConflict = "ExpenseVersionConflict";
    public const string ExpenseIdTaken = "ExpenseIdTaken";
    public const string ExpenseLimitReached = "ExpenseLimitReached";
    public const string TripExpensesFrozen = "TripExpensesFrozen";
    public const string PaymentsNotAcknowledged = "PaymentsNotAcknowledged";
    public const string SharesDoNotAddUp = "SharesDoNotAddUp";
    public const string SharerNotTraveller = "SharerNotTraveller";
    public const string PayerNotTraveller = "PayerNotTraveller";
    public const string LinkNotInTrip = "LinkNotInTrip";
    public const string TooManySharers = "TooManySharers";
    public const string NotEqualSplit = "NotEqualSplit";
    public const string AlreadySharing = "AlreadySharing";
    public const string NotTraveller = "NotTraveller";
    public const string BalancesChanged = "BalancesChanged";
    public const string NothingToPay = "NothingToPay";
    public const string PaymentNotFound = "PaymentNotFound";
    public const string PaymentStateChanged = "PaymentStateChanged";
    public const string NotYourDebt = "NotYourDebt";
    public const string ReminderAlreadySent = "ReminderAlreadySent";
    public const string TooManyRequests = "TooManyRequests";
    public const string BadRequest = "BadRequest";
    // Social contract 2.5: a member joined by request before the trip starts.
    public const string OpensAtStart = "OpensAtStart";

    // NotOnTrip is 409 on S12 and 403 on S14.
    public const int NotOnTripViewStatus = 403;

    private static readonly Dictionary<string, (int Status, string Message)> Table = new()
    {
        [SelfRequest] = (400, "You cannot add yourself."),
        [PersonNotFound] = (404, "No one found."),
        [AlreadyBuddies] = (409, "You are already buddies."),
        [TryLater] = (409, "You can send again later."),
        [WaitingLimit] = (409, "You have 20 requests waiting. Cancel one to send more."),
        [BuddyLimit] = (409, "You have 500 buddies. Remove one to add more."),
        [CannotAddNow] = (409, "Could not add this buddy right now."),
        [BuddyRequestsOnHold] = (403, "You cannot send requests right now."),
        [RequestEnded] = (404, "This request is no longer open."),
        [CannotBlockSelf] = (400, "You cannot block yourself."),
        [CannotReportSelf] = (400, "You cannot report yourself."),
        [TooManyBlocks] = (409, "You have blocked too many people."),
        [InvalidReason] = (400, "Pick a reason."),
        [TripNotFound] = (404, "Trip not found"),
        [TripFull] = (409, "This trip is full."),
        [TripLimitReached] = (409, $"You can keep {TripLimits.MaxTripsPerUser} trips. Delete one to make room."),
        [TripCompleted] = (409, "This trip is complete. Plan changes are off."),
        [NoPlaces] = (400, "Add places to the plan first."),
        [InviteEnded] = (404, "This invite has ended."),
        [NotOnTrip] = (409, "You are no longer on this trip."),
        [TripHasOpenBalances] = (409, "Settle up first. Open payments stay with your travellers."),
        [NotTripOwner] = (403, "Only the trip owner can do this."),
        [ExpenseNotFound] = (404, "Expense not found."),
        [InvalidAmount] = (400, "Enter the amount. Up to ₹5,00,000 for one expense."),
        [TitleRequired] = (400, "Say what it was for."),
        [InvalidCategory] = (400, "Pick a category."),
        [InvalidDay] = (400, "Pick a day of this trip."),
        [NoSharers] = (400, "Pick who shares it."),
        [NoteTooLong] = (400, "Keep the note under 200 characters."),
        [ExpenseVersionConflict] = (409, "Updated on another device, refresh"),
        [ExpenseIdTaken] = (409, "Please try again."),
        [ExpenseLimitReached] = (409, "This trip has 500 expenses. Delete one to add more."),
        [TripExpensesFrozen] = (409, "This trip is closed for changes."),
        [PaymentsNotAcknowledged] = (409, "Payments are already made."),
        [SharesDoNotAddUp] = (422, "The shares must add up to the amount."),
        [SharerNotTraveller] = (422, "This person is no longer on this trip."),
        [PayerNotTraveller] = (422, "The payer is no longer on this trip."),
        [LinkNotInTrip] = (422, "This booking is not part of this trip."),
        [TooManySharers] = (422, "Up to 20 people can share one expense."),
        [NotEqualSplit] = (422, "Custom splits cannot be shared this way."),
        [AlreadySharing] = (422, "Already sharing this expense."),
        [NotTraveller] = (422, "This person is not on the trip."),
        [BalancesChanged] = (409, "The amount changed. Check and confirm again."),
        [NothingToPay] = (409, "Nothing left to pay."),
        [PaymentNotFound] = (404, "Payment not found."),
        [PaymentStateChanged] = (409, "This payment changed. Refresh to see it."),
        [NotYourDebt] = (403, "This is not your payment."),
        [ReminderAlreadySent] = (409, "Reminder sent"),
        [TooManyRequests] = (429, "Too many tries. Please wait."),
        [BadRequest] = (400, "Invalid request."),
        [OpensAtStart] = (409, "This opens when the trip starts."),
    };

    public static IReadOnlyCollection<string> All => Table.Keys;

    public static int StatusOf(string type) => Table.TryGetValue(type, out var e) ? e.Status : 400;

    public static string MessageOf(string type) => Table.TryGetValue(type, out var e) ? e.Message : "Invalid request.";
}
