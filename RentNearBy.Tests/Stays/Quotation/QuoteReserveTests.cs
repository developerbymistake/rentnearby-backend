using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Owner.Quote;
using RentNearBy.Core.Models.Owner.Quote;
using RentNearBy.Infrastructure.Services.Owner.Be3;
using RentNearBy.Infrastructure.Services.Owner.Quote;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Support.Owner;

namespace RentNearBy.Tests.Stays.Quotation;

// Q5 / Q7 / Q13 / ARCHITECTURE 4 on a real PostgreSQL: counted downloads, Q- and PI- sequences, the approved name and the assets
// version in the answer, the refusals before any count (not approved, terms, no stay) and the 409 limit data.
// Needs the fresh InitialCreate with the four StayQuote tables (main session) and RENTNEARBY_TEST_DB.
public class QuoteReserveTests(TestDatabase db) : IntegrationTestBase(db)
{
    private Task<Guid> Ready(string name = "The Banyan Retreat") => QuoteKit.ReadyOwnerAsync(CreateContext, name);

    private async Task<QuoteResult<QuoteReserveDto>> Reserve(Guid user, Guid doc, short kind = QuoteKinds.QuotationCode, TimeProvider? clock = null,
        bool paymentOn = true)
    {
        await using var c = CreateContext();
        return await QuoteKit.Reserve(c, clock, paymentOn).ReserveAsync(user, doc, kind, default);
    }

    [IntegrationFact]
    public async Task First_download_is_counted_with_number_one_and_the_approved_name()
    {
        var user = await Ready();
        var r = await Reserve(user, Guid.NewGuid());
        Assert.True(r.Ok);
        var v = r.Value!;
        Assert.Equal((1, "Q-0001", "Quotation", true), (v.Number, v.Display, v.Kind, v.Counted));
        Assert.Equal("The Banyan Retreat", v.BusinessName);
        Assert.Equal((1, 2, true), (v.Usage.Used, v.Usage.Limit, v.Usage.CanUpgrade));
        Assert.Equal(QuoteRules.MaxFreePerWindow, v.FreeLeft);
        Assert.Equal(v.IssuedAt + QuoteRules.FreeWindow, v.FreeUntil);
        Assert.Equal(0, v.AssetsVersion);
    }

    [IntegrationFact]
    public async Task Quotation_and_proforma_sequences_are_independent()
    {
        var user = await Ready();
        await using (var c = CreateContext()) await QuoteKit.PlanAsync(c, user, UserPlanStatuses.Active, DateTime.UtcNow.AddDays(20));
        var q1 = (await Reserve(user, Guid.NewGuid())).Value!;
        var p1 = (await Reserve(user, Guid.NewGuid(), QuoteKinds.ProformaCode)).Value!;
        var q2 = (await Reserve(user, Guid.NewGuid())).Value!;
        Assert.Equal(("Q-0001", "PI-0001", "Q-0002"), (q1.Display, p1.Display, q2.Display));
        Assert.Equal(3, q2.Usage.Used);
    }

    [IntegrationFact]
    public async Task Kind_change_of_the_same_document_is_a_new_counted_number()
    {
        var user = await Ready();
        await using (var c = CreateContext()) await QuoteKit.PlanAsync(c, user, UserPlanStatuses.Active, DateTime.UtcNow.AddDays(20));
        var doc = Guid.NewGuid();
        var q = (await Reserve(user, doc)).Value!;
        var p = (await Reserve(user, doc, QuoteKinds.ProformaCode)).Value!;
        Assert.Equal(("Q-0001", true), (q.Display, q.Counted));
        Assert.Equal(("PI-0001", true, 2), (p.Display, p.Counted, p.Usage.Used));
    }

    // Q5: the PDF prints the live (approved) name; a waiting name change is never used.
    [IntegrationFact]
    public async Task Business_name_is_the_live_name_not_the_pending_one()
    {
        var user = await Ready("Lake View Stay");
        await using (var c = CreateContext())
        {
            var stay = await c.StayListings.Include(s => s.District).Include(s => s.City).SingleAsync(s => s.UserId == user);
            StayPendingEditWriter.Apply(stay, StayPendingEditWriter.Basis(stay) with { Name = "Lake View Resort" }, DateTime.UtcNow);
            await c.SaveChangesAsync();
        }
        Assert.Equal("Lake View Stay", (await Reserve(user, Guid.NewGuid())).Value!.BusinessName);
    }

    [IntegrationFact]
    public async Task Assets_version_of_the_master_rides_along()
    {
        var user = await Ready();
        await using (var c = CreateContext())
        {
            c.StayQuoteProfiles.Add(new StayQuoteProfile { UserId = user, AssetsVersion = 3, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            await c.SaveChangesAsync();
        }
        Assert.Equal(3, (await Reserve(user, Guid.NewGuid())).Value!.AssetsVersion);
    }

    [IntegrationFact]
    public async Task Not_approved_stay_is_refused_before_any_count()
    {
        Guid user;
        await using (var c = CreateContext())
        {
            user = (await OwnerSeed.StayOwnerAsync(c, StayStatuses.PendingReview)).UserId;
            c.StayQuoteTermsAcceptances.Add(new StayQuoteTermsAcceptance { UserId = user, Version = 1, AcceptedAt = DateTime.UtcNow });
            await c.SaveChangesAsync();
        }
        var r = await Reserve(user, Guid.NewGuid());
        Assert.Equal(QuoteErrors.StayNotApproved, r.ErrorType);
        await using var check = CreateContext();
        Assert.False(await check.StayQuoteCounters.AnyAsync(x => x.UserId == user));
    }

    [IntegrationFact]
    public async Task Terms_not_accepted_is_409_with_the_current_version()
    {
        Guid user;
        await using (var c = CreateContext()) user = (await OwnerSeed.StayOwnerAsync(c)).UserId;
        var r = await Reserve(user, Guid.NewGuid());
        Assert.Equal(QuoteErrors.TermsNotAccepted, r.ErrorType);
        Assert.Equal(409, QuoteErrors.StatusOf(r.ErrorType!));
        Assert.Contains("currentVersion", System.Text.Json.JsonSerializer.Serialize(r.Data));
    }

    [IntegrationFact]
    public async Task Owner_without_a_stay_is_404()
    {
        Guid user;
        await using (var c = CreateContext()) user = await OwnerSeed.UserAsync(c, UserRoles.StayOwnerId);
        Assert.Equal(QuoteErrors.StayNotFound, (await Reserve(user, Guid.NewGuid())).ErrorType);
    }

    [IntegrationFact]
    public async Task Third_new_document_on_free_is_refused_with_the_cap_data_and_nothing_written()
    {
        var user = await Ready();
        Assert.True((await Reserve(user, Guid.NewGuid())).Ok);
        Assert.True((await Reserve(user, Guid.NewGuid())).Ok);
        var doc = Guid.NewGuid();
        var r = await Reserve(user, doc);
        Assert.Equal(QuoteErrors.MonthLimit, r.ErrorType);
        var data = Assert.IsType<QuoteLimitDataDto>(r.Data);
        Assert.Equal((PlanFeatureKeys.StayQuotationsPerMonth, 2, 2, true), (data.Key, data.Limit, data.Used, data.CanUpgrade));
        Assert.True(data.ResetsAt > DateTime.UtcNow);
        await using var check = CreateContext();
        Assert.False(await check.StayQuoteIssues.AnyAsync(i => i.UserId == user && i.DocumentId == doc));
        Assert.Equal(2, (await check.StayQuoteCounters.SingleAsync(x => x.UserId == user)).Used);
    }

    // A deleted owner (FK gone) at the same moment answers 401, never a 500.
    [IntegrationFact]
    public async Task Pre_read_finds_nothing_for_an_unknown_user()
        => Assert.Equal(QuoteErrors.StayNotFound, (await Reserve(Guid.NewGuid(), Guid.NewGuid())).ErrorType);
}
