using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Core.DTOs.Social;
using RentNearBy.Core.Entities.Social;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.Social;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Support.Buddy;
using RentNearBy.Tests.Support.Social;

namespace RentNearBy.Tests.Social;

// Contract 2.2: onboarding once, identity locked, under 18 stores nothing, admin identity correction audited.
// Needs RENTNEARBY_TEST_DB; skipped without it.
public class ProfileIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private static SocialProfileService Profiles(ApplicationDbContext ctx)
        => new(ctx, SocialTestSeams.Limits(10), new PublicTripReader(ctx, TimeProvider.System), SocialTestSeams.AfterCommit(ctx),
            TimeProvider.System, NullLogger<SocialProfileService>.Instance);

    private static SocialOnboardingRequest Onboarding(int birthYear = 1995) => new()
    {
        Gender = SocialGenders.Female, BirthMonth = 5, BirthYear = birthYear, Interests = ["Mountains", "Treks", "Food"],
        Skills = ["Photographer"], Places = ["Manali"], About = "Hills over beaches. Chai at every stop.",
    };

    private async Task<Guid> AccountAsync()
    {
        await using var ctx = CreateContext();
        return (await BuddySeed.UsersAsync(ctx, 1))[0];
    }

    [IntegrationFact]
    public async Task Onboarding_runs_once_and_a_second_call_is_identity_locked()
    {
        var me = await AccountAsync();
        await using (var ctx = CreateContext())
        {
            var first = await Profiles(ctx).OnboardAsync(me, Onboarding());
            Assert.True(first.Ok, first.ErrorType);
            Assert.Equal(SocialGenders.Female, first.Value!.Gender);
            var again = await Profiles(ctx).OnboardAsync(me, Onboarding(1990));
            Assert.Equal(SocialErrorTypes.IdentityLocked, again.ErrorType);
        }
        await using var check = CreateContext();
        var row = await check.Set<UserProfile>().AsNoTracking().SingleAsync(p => p.UserId == me);
        Assert.Equal((SocialGenders.Female, (short)5, (short)1995), (row.Gender, row.BirthMonth, row.BirthYear));
    }

    [IntegrationFact]
    public async Task Under_18_stores_nothing()
    {
        var me = await AccountAsync();
        await using (var ctx = CreateContext())
        {
            var result = await Profiles(ctx).OnboardAsync(me, Onboarding(DateTime.UtcNow.Year - 10));
            Assert.Equal(SocialErrorTypes.Under18, result.ErrorType);
        }
        await using var check = CreateContext();
        Assert.False(await check.Set<UserProfile>().AnyAsync(p => p.UserId == me));
    }

    [IntegrationFact]
    public async Task Profile_edit_refuses_gender_and_birth()
    {
        var me = await AccountAsync();
        await using var ctx = CreateContext();
        Assert.True((await Profiles(ctx).OnboardAsync(me, Onboarding())).Ok);
        var edit = await Profiles(ctx).UpdateProfileAsync(me, new UpdateSocialProfileRequest { Gender = SocialGenders.Male });
        Assert.Equal(SocialErrorTypes.IdentityLocked, edit.ErrorType);
        var birth = await Profiles(ctx).UpdateProfileAsync(me, new UpdateSocialProfileRequest { BirthYear = 1990 });
        Assert.Equal(SocialErrorTypes.IdentityLocked, birth.ErrorType);
    }

    [IntegrationFact]
    public async Task Admin_identity_correction_changes_the_row_and_writes_one_audit()
    {
        var me = await AccountAsync();
        var admin = Guid.NewGuid();
        await using (var ctx = CreateContext())
        {
            Assert.True((await Profiles(ctx).OnboardAsync(me, Onboarding())).Ok);
            var fixedRow = await Profiles(ctx).AdminSetIdentityAsync(admin, me,
                new AdminIdentityRequest { Gender = SocialGenders.Male, BirthYear = 1993, Reason = "Support ticket: wrong gender" });
            Assert.True(fixedRow.Ok, fixedRow.ErrorType);
            Assert.Equal((SocialGenders.Male, 5, 1993), (fixedRow.Value!.Gender, fixedRow.Value.BirthMonth, fixedRow.Value.BirthYear));
            Assert.Equal(SocialErrorTypes.ProfileRequired,
                (await Profiles(ctx).AdminSetIdentityAsync(admin, Guid.NewGuid(), new AdminIdentityRequest { Reason = "none" })).ErrorType);
        }
        await using var check = CreateContext();
        var audit = await check.Set<ProfileIdentityAudit>().AsNoTracking().SingleAsync(a => a.UserId == me);
        Assert.Equal((SocialGenders.Female, SocialGenders.Male), (audit.OldGender, audit.NewGender));
        Assert.Equal(((short)1995, (short)1993), (audit.OldBirthYear, audit.NewBirthYear));
        Assert.Equal(admin, audit.AdminId);
        Assert.Equal(SocialGenders.Male, (await check.Set<UserProfile>().AsNoTracking().SingleAsync(p => p.UserId == me)).Gender);
    }

    [IntegrationFact]
    public async Task Admin_identity_correction_refuses_an_under_18_or_future_birth_and_stores_nothing()
    {
        var me = await AccountAsync();
        var admin = Guid.NewGuid();
        await using (var ctx = CreateContext())
        {
            Assert.True((await Profiles(ctx).OnboardAsync(me, Onboarding())).Ok);
            var young = await Profiles(ctx).AdminSetIdentityAsync(admin, me,
                new AdminIdentityRequest { BirthYear = DateTime.UtcNow.Year - 10, Reason = "typo" });
            Assert.Equal(SocialErrorTypes.Under18, young.ErrorType);
            Assert.Equal(400, SocialErrorTypes.StatusOf(young.ErrorType!));
            var future = await Profiles(ctx).AdminSetIdentityAsync(admin, me,
                new AdminIdentityRequest { BirthYear = 2099, Reason = "typo" });
            Assert.Equal(SocialErrorTypes.Under18, future.ErrorType);
        }
        await using var check = CreateContext();
        Assert.False(await check.Set<ProfileIdentityAudit>().AnyAsync(a => a.UserId == me));
        Assert.Equal((short)1995, (await check.Set<UserProfile>().AsNoTracking().SingleAsync(p => p.UserId == me)).BirthYear);
    }

    [IntegrationFact]
    public async Task Failed_profile_edit_writes_nothing_not_even_the_name()
    {
        var me = await AccountAsync();
        string? before;
        await using (var ctx = CreateContext())
        {
            Assert.True((await Profiles(ctx).OnboardAsync(me, Onboarding())).Ok);
            before = await ctx.Users.AsNoTracking().Where(u => u.Id == me).Select(u => u.Name).SingleAsync();
        }
        await using (var ctx = CreateContext())
        {
            var bad = await Profiles(ctx).UpdateProfileAsync(me, new UpdateSocialProfileRequest { Name = "Brand New", Interests = ["bad"] });
            Assert.Equal(SocialErrorTypes.InvalidProfile, bad.ErrorType);
        }
        await using (var check = CreateContext())
            Assert.Equal(before, await check.Users.AsNoTracking().Where(u => u.Id == me).Select(u => u.Name).SingleAsync());
        await using (var ctx = CreateContext())
            Assert.True((await Profiles(ctx).UpdateProfileAsync(me, new UpdateSocialProfileRequest { Name = "Brand New" })).Ok);
        await using var after = CreateContext();
        Assert.Equal("Brand New", await after.Users.AsNoTracking().Where(u => u.Id == me).Select(u => u.Name).SingleAsync());
    }

    [IntegrationTheory]
    [InlineData("me@gmail.com")]
    [InlineData("wa.me/919876543210")]
    [InlineData("https://example.com")]
    [InlineData("@myhandle")]
    public async Task Home_city_with_contact_details_is_text_not_allowed_and_never_stored(string city)
    {
        var me = await AccountAsync();
        await using (var ctx = CreateContext())
        {
            Assert.True((await Profiles(ctx).OnboardAsync(me, Onboarding())).Ok);
            var edit = await Profiles(ctx).UpdateProfileAsync(me, new UpdateSocialProfileRequest { HomeCityName = city });
            Assert.Equal(SocialErrorTypes.TextNotAllowed, edit.ErrorType);
            var state = await Profiles(ctx).UpdateProfileAsync(me, new UpdateSocialProfileRequest { HomeStateName = city });
            Assert.Equal(SocialErrorTypes.TextNotAllowed, state.ErrorType);
        }
        var other = await AccountAsync();
        await using (var ctx = CreateContext())
        {
            var onboarding = Onboarding();
            onboarding.HomeCityName = city;
            Assert.Equal(SocialErrorTypes.TextNotAllowed, (await Profiles(ctx).OnboardAsync(other, onboarding)).ErrorType);
        }
        await using var check = CreateContext();
        Assert.Null((await check.Set<UserProfile>().AsNoTracking().SingleAsync(p => p.UserId == me)).HomeCityName);
        Assert.False(await check.Set<UserProfile>().AnyAsync(p => p.UserId == other));
    }
}
