using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using RentNearBy.Api.Handlers;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Services.Buddy.Be1;
using RentNearBy.Infrastructure.Services.Buddy.Be2;
using RentNearBy.Infrastructure.Services.Buddy.Be4;

namespace RentNearBy.Tests.SeekerOnly;

// BE-T6 (contract sections 2.4, 3, 4): invites and requests only between seeker accounts, a non-seeker reads like an unknown
// or inactive person (same statement, same answer), and the old data of a role-changed account is hidden, never deleted.
public class SeekerInviteRulesTests
{
    public static TheoryData<string, string> Statements => new()
    {
        { "BuddyFindService.FindSql", BuddyFindService.FindSql },
        { "BuddyRequestService.SendStateSql", BuddyRequestService.SendStateSql },
        { "BuddyRequestService.AcceptStateSql", BuddyRequestService.AcceptStateSql },
        { "BuddyListService.ReceivedSql", BuddyListService.ReceivedSql },
        { "BuddyListService.SentSql", BuddyListService.SentSql },
        { "BuddyListService.SummarySql", BuddyListService.SummarySql },
        { "BuddyListService.BuddiesSql", BuddyListService.BuddiesSql },
        { "TripMemberService.IsBuddySql", TripMemberService.IsBuddySql },
        { "TripInviteService.CandidatesSql", TripInviteService.CandidatesSql },
        { "Be2Sql.InviteRest", Be2Sql.InviteRest },
        { "TripSharedListReader.InvitesSql", TripSharedListReader.InvitesSql },
    };

    [Theory]
    [MemberData(nameof(Statements))]
    public void Every_buddy_and_sharing_statement_tests_the_other_person_is_a_seeker(string name, string sql)
        => Assert.True(sql.Contains($"\"RoleId\" = {TravellerOnly.SeekerRoleSql}") || sql.Contains($"\"RoleId\" <> {TravellerOnly.SeekerRoleSql}"), name);

    [Fact]
    public void Seeker_fragment_is_the_seeker_role_id() => Assert.Equal($"'{UserRoles.SeekerId}'", TravellerOnly.SeekerRoleSql);

    // The counts equal the list: each buddy count joins the person with the same role test.
    [Fact]
    public void Buddy_counts_carry_the_same_role_test_as_the_list()
    {
        Assert.Equal(3, Count(BuddyListService.SummarySql, TravellerOnly.SeekerRoleSql));
        Assert.Equal(2, Count(BuddyListService.BuddiesSql, TravellerOnly.SeekerRoleSql));
        Assert.Contains("OR o.\"RoleId\" <>", Be2Sql.InviteRest);
        Assert.Contains(Be2Sql.InviteRest, Be2Sql.InviteSql);
        Assert.Contains(Be2Sql.InviteRest, Be2Sql.InviteLeanSql);
    }

    // M9: a JOINED trip keeps its slot whatever the owner's role; SlotsSql has no seeker filter.
    [Fact]
    public void Trip_slots_are_not_filtered_by_role() => Assert.DoesNotContain(TravellerOnly.SeekerRoleSql, TripSlots.SlotsSql);

    // The new role test never adds an active-account test where there was none (paused accounts keep today's behaviour).
    [Fact]
    public void Sent_and_buddy_lists_add_only_the_role_test()
    {
        Assert.DoesNotContain("IsActive", BuddyListService.SentSql);
        Assert.DoesNotContain("IsActive", BuddyListService.BuddiesSql);
        Assert.DoesNotContain("IsActive", BuddyRequestService.AcceptStateSql);
    }

    [Fact]
    public void Inactive_or_non_seeker_candidate_is_not_a_buddy_for_an_invite()
    {
        var owner = Guid.NewGuid();
        var candidate = new InviteCandidate(Guid.NewGuid(), Exists: true, IsActive: false, IsBuddy: true, Blocked: false, null, null, null);
        Assert.Equal(InviteOutcomes.NotBuddy, TripSharingRules.Classify(candidate, owner, DateTime.UtcNow));
        Assert.Equal(InviteOutcomes.Invited, TripSharingRules.Classify(candidate with { IsActive = true }, owner, DateTime.UtcNow));
    }

    private static readonly Guid Guest = Guid.NewGuid();
    private static readonly Guid Host = Guid.NewGuid();

    private static ClaimsPrincipal As(Guid id, string role)
        => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id.ToString()), new Claim(ClaimTypes.Role, role)], "test"));

    private static TripEnquiry Enquiry() => new() { Id = Guid.NewGuid(), TravellerUserId = Guest, OwnerUserId = Host };

    [Fact]
    public void Traveller_side_of_a_non_seeker_is_refused_with_the_seeker_403()
    {
        var denied = TripEnquiryAuth.RequireParty(Enquiry(), Guest, As(Guest, UserRoles.StayOwner), out var side);
        Assert.Equal(TripEnquiryAuth.Traveller, side);
        Assert.NotNull(denied);
        Assert.Equal(403, Assert.IsAssignableFrom<IStatusCodeHttpResult>(denied).StatusCode);
        var body = Assert.IsType<ApiResponse<object>>(Assert.IsAssignableFrom<IValueHttpResult>(denied).Value);
        Assert.Equal(AuthPolicies.ErrorType, body.Error!.Type);
        Assert.Equal(AuthPolicies.SeekerOnlyMessage, body.Error.Message);
    }

    [Fact]
    public void Traveller_side_of_a_seeker_and_owner_side_of_an_owner_pass()
    {
        Assert.Null(TripEnquiryAuth.RequireParty(Enquiry(), Guest, As(Guest, UserRoles.Seeker), out var guestSide));
        Assert.Equal(TripEnquiryAuth.Traveller, guestSide);
        Assert.Null(TripEnquiryAuth.RequireParty(Enquiry(), Host, As(Host, UserRoles.StayOwner), out var hostSide));
        Assert.Equal(TripEnquiryAuth.Owner, hostSide);
    }

    [Fact]
    public void Stranger_and_missing_enquiry_answers_are_unchanged()
    {
        var stranger = Guid.NewGuid();
        var forbidden = TripEnquiryAuth.RequireParty(Enquiry(), stranger, As(stranger, UserRoles.StayOwner), out _);
        var body = Assert.IsType<ApiResponse<object>>(Assert.IsAssignableFrom<IValueHttpResult>(forbidden).Value);
        Assert.Equal("Forbidden", body.Error!.Type);
        var missing = TripEnquiryAuth.RequireParty(null, stranger, As(stranger, UserRoles.Seeker), out _);
        Assert.Equal(404, Assert.IsAssignableFrom<IStatusCodeHttpResult>(missing).StatusCode);
    }

    // M12: the chat list leaves out the guest card of a non-seeker; the host side keeps it.
    [Fact]
    public void Chat_card_is_visible_to_a_seeker_guest_and_to_the_host_only()
    {
        Assert.False(TripEnquiryAuth.VisibleTo(Enquiry(), Guest, As(Guest, UserRoles.StayOwner)));
        Assert.False(TripEnquiryAuth.VisibleTo(Enquiry(), Guest, As(Guest, UserRoles.TaxiDriver)));
        Assert.True(TripEnquiryAuth.VisibleTo(Enquiry(), Guest, As(Guest, UserRoles.Seeker)));
        Assert.True(TripEnquiryAuth.VisibleTo(Enquiry(), Host, As(Host, UserRoles.StayOwner)));
    }

    // Every NotificationTypes constant is classified here by its writer; a new type fails until it is put in one list.
    private static readonly string[] TravellerTypes =
    [
        NotificationTypes.TripOfferReceived, NotificationTypes.TripListingRemoved, NotificationTypes.TripEnquiryExpired,
        NotificationTypes.TripNotAvailable, NotificationTypes.TripOwnerCancelled, NotificationTypes.TripDriverFare,
        NotificationTypes.TripDriverConfirmed, NotificationTypes.TripDriverDenied, NotificationTypes.TripRevisedOffer,
        NotificationTypes.TripChangeAccepted, NotificationTypes.TripNoReply, NotificationTypes.TripStartsIn3Days,
        NotificationTypes.TripStartsToday, NotificationTypes.TripMarkComplete, NotificationTypes.TripTravelEve,
        NotificationTypes.TaxiDriverAccepted, NotificationTypes.TaxiSelectionLapsed, NotificationTypes.TaxiBooked,
        NotificationTypes.TaxiRequestEnded, NotificationTypes.TaxiDriverWithdrew, NotificationTypes.TaxiRateDriver,
        NotificationTypes.TaxiChooseDriver,
        NotificationTypes.BuddyRequestReceived, NotificationTypes.BuddyRequestAccepted, NotificationTypes.TripInvite,
        NotificationTypes.TripMemberJoined, NotificationTypes.TripPlanUpdated, NotificationTypes.TripMemberRemoved,
        NotificationTypes.TripDeletedForViewer, NotificationTypes.TripExpensesUpdated, NotificationTypes.TripSettleReminder,
        NotificationTypes.TripPaymentSaidPaid, NotificationTypes.TripPaymentReceived, NotificationTypes.TripPaymentWithdrawn,
        NotificationTypes.TripPaymentNotReceived,
        NotificationTypes.SocialFollowRequest, NotificationTypes.SocialNewFollower, NotificationTypes.SocialFollowAccepted,
        NotificationTypes.SocialJoinRequest, NotificationTypes.SocialJoinAccepted, NotificationTypes.SocialChatMessage,
        NotificationTypes.SocialTripChatMessage,
    ];

    // Sent to hosts, drivers, admins or any role (TripReminder and TaxiTripEnded reach both sides).
    private static readonly string[] EveryoneTypes =
    [
        NotificationTypes.RoleRequestApproved, NotificationTypes.RoleRequestRejected, NotificationTypes.StayApproved,
        NotificationTypes.StayRejected, NotificationTypes.TaxiApproved, NotificationTypes.TaxiRejected,
        NotificationTypes.DriverPhotoRemoved, NotificationTypes.ProfilePhotoRemoved, NotificationTypes.PlanActivated,
        NotificationTypes.PlanExpiring, NotificationTypes.PlanExpired, NotificationTypes.PlanRenewed,
        NotificationTypes.PlanPaymentIssue, NotificationTypes.PlanOnHold, NotificationTypes.PlanPaused,
        NotificationTypes.PlanCarryOverActivated, NotificationTypes.StayJobPosted, NotificationTypes.PlanPurchased,
        NotificationTypes.PlanChargebackReview, NotificationTypes.TripEnquiryReceived, NotificationTypes.TripOfferAccepted,
        NotificationTypes.TripOfferDeclined, NotificationTypes.TripTravellerLeft, NotificationTypes.TripTravellerCancelled,
        NotificationTypes.TripLeadReceived, NotificationTypes.TripTaxiBookedByTraveller, NotificationTypes.TripBookingChanged,
        NotificationTypes.TripEnquiryClosedByAdmin, NotificationTypes.TripCounterReceived, NotificationTypes.TripRevisedKept,
        NotificationTypes.TripChangeRequest, NotificationTypes.TripProblemReported, NotificationTypes.TripReminder,
        NotificationTypes.TaxiTripEnded, NotificationTypes.TaxiSelected, NotificationTypes.TaxiNotSelected,
        NotificationTypes.TaxiRequestClosed, NotificationTypes.TaxiBookingCancelled, NotificationTypes.TaxiRingsMissed,
        NotificationTypes.TaxiRequestRing, NotificationTypes.TaxiRequestRingEnded, NotificationTypes.StayEditApproved,
        NotificationTypes.StayEditRejected, NotificationTypes.FestivalReminder, NotificationTypes.PlanLimitsChanged,
        NotificationTypes.OwnerMorningReminder, NotificationTypes.PlansStarting, NotificationTypes.OwnerBookingTomorrow,
        NotificationTypes.StayEnquiryRing, NotificationTypes.StayEnquiryRingEnded, NotificationTypes.StayEnquiryRingMissed,
    ];

    [Fact]
    public void Every_notification_type_is_classified_once_and_the_traveller_list_is_the_hidden_set()
    {
        var constants = typeof(NotificationTypes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!).ToList();
        foreach (var type in constants)
            Assert.True(TravellerTypes.Contains(type) ^ EveryoneTypes.Contains(type), $"{type} must be in exactly one list");
        Assert.Equal(constants.Count, TravellerTypes.Length + EveryoneTypes.Length);
        Assert.Equal(TravellerTypes.Order(), TravellerOnly.NotificationTypes.Order());
        Assert.Empty(TravellerOnly.NotificationTypes.Intersect(NotificationTypes.OwnerPushKinds));
    }

    [Fact]
    public void Notice_kinds_by_role()
    {
        Assert.Equal(NoticeKinds.All, NoticeReader.KindsFor(seeker: true));
        Assert.Equal(NoticeKinds.Owner, NoticeReader.KindsFor(seeker: false));
        Assert.Equal(NoticeKinds.All.Except(NoticeKinds.Owner).Order(), TravellerOnly.NoticeKinds.Order());
        Assert.Empty(TravellerOnly.NoticeKinds.Intersect(NoticeKinds.Owner));
    }

    [Fact]
    public void Notice_statements_filter_by_kind()
    {
        foreach (var sql in new[] { NoticeReader.FirstPageSql, NoticeReader.NextPageSql })
            Assert.Contains("n.\"Kind\" = ANY(@kinds)", sql);
        Assert.Contains("\"Kind\" = ANY(@kinds)", NoticeReader.UnreadSql);
        Assert.Contains("\"Kind\" = ANY(@kinds)", NoticeReader.MarkAllReadSql);
        Assert.DoesNotContain("@kinds", NoticeReader.MarkReadSql);
    }

    private static int Count(string text, string part)
    {
        var n = 0;
        for (var i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal)) n++;
        return n;
    }
}
