using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Owner.Quote;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Core.Models.Owner.Quote;
using RentNearBy.Infrastructure.Services.Owner.Be3;
using RentNearBy.Infrastructure.Services.Owner.Quote;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Owner.Be2;
using RentNearBy.Tests.Support.Owner;

namespace RentNearBy.Tests.Stays.Quotation;

// Q4 / Q5 / Q6 / Q19 / Q21 on a real PostgreSQL: master save and reload, bank intent (Keep / Set / Clear) encrypted at rest,
// D-6 GSTIN match, the hub (name from the stay row, pending name apart, masked bank only, ETag 304), terms acceptance kept once,
// the name-change request on the shared pending-edit path, and one owner never seeing another's data.
public class QuoteProfileTests(TestDatabase db) : IntegrationTestBase(db)
{
    private static QuoteProfileRequest Master(string? gstin = null) => new()
    {
        Slogan = "Stay close to the forest", Address = "Village Dhikuli\nRamnagar", Phone = "9876543210", Email = "Stay@Example.com",
        Gstin = gstin, TermsText = "50% advance confirms the booking.", CancellationText = "15 days or more: full refund.",
        BankMode = QuoteBankModes.Keep,
    };

    private static QuoteBankRequest Bank() => new()
    {
        Holder = "Banyan Retreat", AccountNo = "123456789012", Ifsc = "SBIN0001234", Bank = "SBI", Branch = "Ramnagar", Upi = "banyan@sbi",
    };

    private async Task<Guid> Owner(string name = "The Banyan Retreat", string? stayGstin = null)
    {
        await using var c = CreateContext();
        var owner = await OwnerSeed.StayOwnerAsync(c, name: name);
        if (stayGstin != null)
        {
            var stay = await c.StayListings.SingleAsync(s => s.Id == owner.ListingId);
            stay.Gstin = stayGstin;
            await c.SaveChangesAsync();
        }
        return owner.UserId;
    }

    private async Task<T> With<T>(Func<StayQuoteProfileService, Task<T>> act, bool cipherOn = true, RecordingOwnerNotifier? notifier = null)
    {
        await using var c = CreateContext();
        return await act(QuoteKit.Profile(c, cipherOn: cipherOn, notifier: notifier));
    }

    [IntegrationFact]
    public async Task Save_and_reload_the_master_through_the_hub()
    {
        var user = await Owner();
        var saved = await With(s => s.SaveProfileAsync(user, Master(), default));
        Assert.True(saved.Ok);
        Assert.Equal("stay@example.com", saved.Value!.Email);

        var hub = (await With(s => s.GetHubAsync(user, null, default))).Value!.Hub!;
        Assert.Equal("Stay close to the forest", hub.Profile!.Slogan);
        Assert.Equal("Village Dhikuli\nRamnagar", hub.Profile.Address);
        Assert.False(hub.Profile.Bank.Has);
        Assert.Equal("Host", hub.PreparedBy);
        Assert.Equal((1, 1), (hub.Next.Quotation, hub.Next.Proforma));
        Assert.Equal((QuoteRules.MinTemplateVersion, QuoteRules.PreviewWatermark, QuoteRules.MaxFreePerWindow),
            (hub.MinTemplateVersion, hub.PreviewWatermark, hub.MaxFreePerWindow));
    }

    [IntegrationFact]
    public async Task Hub_before_the_first_save_has_no_profile_and_offers_the_stay_defaults()
    {
        var user = await Owner();
        var hub = (await With(s => s.GetHubAsync(user, null, default))).Value!.Hub!;
        Assert.Null(hub.Profile);
        Assert.NotNull(hub.Defaults.Phone);
        Assert.Equal((0, 2, true), (hub.Usage.Used, hub.Usage.Limit, hub.Usage.CanUpgrade));
        Assert.Null(hub.Terms.AcceptedVersion);
        Assert.Equal(QuoteTerms.CurrentVersion, hub.Terms.CurrentVersion);
    }

    // Q5: the business name is the stay row's live name; a waiting change shows apart as pendingName, never as the name.
    [IntegrationFact]
    public async Task Hub_name_is_the_live_stay_name_and_the_pending_one_is_apart()
    {
        var user = await Owner("Lake View Stay");
        var change = await With(s => s.RequestNameChangeAsync(user, new QuoteNameChangeRequest { Name = "Lake View Resort", Note = "New board" }, default));
        Assert.Equal("Lake View Resort", change.Value!.PendingName);
        var hub = (await With(s => s.GetHubAsync(user, null, default))).Value!.Hub!;
        Assert.Equal(("Lake View Stay", "Lake View Resort", true), (hub.Stay.Name, hub.Stay.PendingName, hub.Stay.Approved));
    }

    [IntegrationFact]
    public async Task Bank_set_is_encrypted_masked_in_the_hub_and_returned_whole_only_by_the_bank_route()
    {
        var user = await Owner();
        var set = Master();
        set.BankMode = QuoteBankModes.Set;
        set.Bank = Bank();
        var saved = (await With(s => s.SaveProfileAsync(user, set, default))).Value!;
        Assert.Equal(("9012", "ba***@sbi"), (saved.Bank.AccountLast4, saved.Bank.UpiMasked));

        await using (var c = CreateContext())
        {
            var row = await c.StayQuoteProfiles.SingleAsync(p => p.UserId == user);
            Assert.True(row.HasBank);
            Assert.DoesNotContain("123456789012", row.BankEnc);
            Assert.DoesNotContain("banyan@sbi", row.BankEnc);
        }

        var hubJson = System.Text.Json.JsonSerializer.Serialize((await With(s => s.GetHubAsync(user, null, default))).Value!.Hub);
        Assert.DoesNotContain("123456789012", hubJson);
        Assert.DoesNotContain("banyan@sbi", hubJson);

        var bank = (await With(s => s.GetBankAsync(user, default))).Value!;
        Assert.Equal(("123456789012", "SBIN0001234", "banyan@sbi"), (bank.AccountNo, bank.Ifsc, bank.Upi));
    }

    [IntegrationFact]
    public async Task Keep_leaves_the_bank_as_stored_and_clear_removes_it()
    {
        var user = await Owner();
        var set = Master();
        set.BankMode = QuoteBankModes.Set;
        set.Bank = Bank();
        await With(s => s.SaveProfileAsync(user, set, default));

        var keep = Master();
        keep.Slogan = "Changed";
        Assert.True((await With(s => s.SaveProfileAsync(user, keep, default))).Value!.Bank.Has);

        var clear = Master();
        clear.BankMode = QuoteBankModes.Clear;
        Assert.False((await With(s => s.SaveProfileAsync(user, clear, default))).Value!.Bank.Has);
        await using var c = CreateContext();
        var row = await c.StayQuoteProfiles.SingleAsync(p => p.UserId == user);
        Assert.False(row.HasBank);
        Assert.Null(row.BankEnc);
    }

    [IntegrationFact]
    public async Task Cipher_off_refuses_the_bank_and_saves_nothing()
    {
        var user = await Owner();
        var set = Master();
        set.BankMode = QuoteBankModes.Set;
        set.Bank = Bank();
        var r = await With(s => s.SaveProfileAsync(user, set, default), cipherOn: false);
        Assert.Equal(QuoteErrors.BankUnavailable, r.ErrorType);
        await using var c = CreateContext();
        Assert.False(await c.StayQuoteProfiles.AnyAsync(p => p.UserId == user));
        Assert.False((await With(s => s.GetHubAsync(user, null, default), cipherOn: false)).Value!.Hub!.BankStorage);
    }

    [IntegrationFact]
    public async Task Gstin_must_match_the_stay_gstin_when_the_stay_has_one()
    {
        var user = await Owner(stayGstin: "27AAPFU0939F1ZV");
        Assert.Equal(QuoteErrors.GstinMismatch, (await With(s => s.SaveProfileAsync(user, Master("05AAAAA1234A1Z1"), default))).ErrorType);
        Assert.True((await With(s => s.SaveProfileAsync(user, Master("27aapfu0939f1zv"), default))).Ok);
        Assert.True((await With(s => s.SaveProfileAsync(user, Master(), default))).Ok);
    }

    [IntegrationFact]
    public async Task Hub_etag_answers_not_modified_until_something_changes()
    {
        var user = await Owner();
        var first = (await With(s => s.GetHubAsync(user, null, default))).Value!;
        Assert.False(first.NotModified);
        Assert.StartsWith("W/\"", first.ETag);
        var again = (await With(s => s.GetHubAsync(user, first.ETag, default))).Value!;
        Assert.True(again.NotModified);
        Assert.Null(again.Hub);
        await With(s => s.SaveProfileAsync(user, Master(), default));
        Assert.False((await With(s => s.GetHubAsync(user, first.ETag, default))).Value!.NotModified);
    }

    [IntegrationFact]
    public async Task Terms_accept_stores_once_keeps_the_first_time_and_refuses_an_unknown_version()
    {
        var user = await Owner();
        var first = (await With(s => s.AcceptTermsAsync(user, new QuoteTermsAcceptRequest { Version = 1, AppVersion = "1.2.0" }, default))).Value!;
        await Task.Delay(20);
        var second = (await With(s => s.AcceptTermsAsync(user, new QuoteTermsAcceptRequest { Version = 1 }, default))).Value!;
        Assert.Equal(first.AcceptedAt, second.AcceptedAt);
        Assert.Equal(QuoteErrors.TermsVersionUnknown, (await With(s => s.AcceptTermsAsync(user, new QuoteTermsAcceptRequest { Version = 2 }, default))).ErrorType);
        await using var c = CreateContext();
        var row = await c.StayQuoteTermsAcceptances.SingleAsync(a => a.UserId == user);
        Assert.Equal(("1.2.0", (short)1), (row.AppVersion, row.Version));
        var hub = (await With(s => s.GetHubAsync(user, null, default))).Value!.Hub!;
        Assert.Equal(1, hub.Terms.AcceptedVersion);
    }

    // Name change rides the shared writer: other waiting fields are kept, the note is stored, the reject notice is cleared,
    // a not approved stay is refused, the admin row carries note and the mixed-script flag.
    [IntegrationFact]
    public async Task Name_change_keeps_other_waiting_fields_and_stores_the_note()
    {
        var user = await Owner("Lake View Stay");
        await using (var c = CreateContext())
        {
            var stay = await c.StayListings.Include(s => s.District).Include(s => s.City).SingleAsync(s => s.UserId == user);
            StayPendingEditWriter.Apply(stay, StayPendingEditWriter.Basis(stay) with { Address = "New road 1" }, DateTime.UtcNow);
            await c.SaveChangesAsync();
        }
        var notifier = new RecordingOwnerNotifier();
        var r = await With(s => s.RequestNameChangeAsync(user, new QuoteNameChangeRequest { Name = "Тake View", Note = "Board" }, default), notifier: notifier);
        Assert.True(r.Ok);
        await using var check = CreateContext();
        var json = (await check.StayListings.SingleAsync(s => s.UserId == user)).PendingEditJson;
        var pending = StayPendingEditJson.Read(json)!;
        Assert.Equal(new[] { StayReviewFields.Name, StayReviewFields.Address }, pending.Fields);
        Assert.Equal(("Тake View", "New road 1", "Board"), (pending.Asked.Name, pending.Asked.Address, pending.Note));
        Assert.Contains(notifier.Cleared, x => x.Kind == RentNearBy.Core.Models.Buddy.NoticeKinds.StayEditRejected);
        Assert.True(StayNameScript.IsMixed(pending.Asked.Name));
    }

    [IntegrationFact]
    public async Task Name_change_on_a_stay_under_review_is_refused()
    {
        Guid user;
        await using (var c = CreateContext()) user = (await OwnerSeed.StayOwnerAsync(c, StayStatuses.PendingReview)).UserId;
        Assert.Equal(QuoteErrors.StayNotApproved,
            (await With(s => s.RequestNameChangeAsync(user, new QuoteNameChangeRequest { Name = "Other" }, default))).ErrorType);
    }

    [IntegrationFact]
    public async Task One_owner_never_sees_another_owners_master()
    {
        var a = await Owner("Stay A");
        var b = await Owner("Stay B");
        var set = Master();
        set.BankMode = QuoteBankModes.Set;
        set.Bank = Bank();
        await With(s => s.SaveProfileAsync(a, set, default));
        var hubB = (await With(s => s.GetHubAsync(b, null, default))).Value!.Hub!;
        Assert.Null(hubB.Profile);
        Assert.Equal("Stay B", hubB.Stay.Name);
        Assert.Null((await With(s => s.GetBankAsync(b, default))).Value);
    }

    [IntegrationFact]
    public async Task Owner_without_a_stay_gets_404_on_hub_and_save()
    {
        Guid user;
        await using (var c = CreateContext()) user = await OwnerSeed.UserAsync(c, UserRoles.StayOwnerId);
        Assert.Equal(QuoteErrors.StayNotFound, (await With(s => s.GetHubAsync(user, null, default))).ErrorType);
        Assert.Equal(QuoteErrors.StayNotFound, (await With(s => s.SaveProfileAsync(user, Master(), default))).ErrorType);
    }
}
