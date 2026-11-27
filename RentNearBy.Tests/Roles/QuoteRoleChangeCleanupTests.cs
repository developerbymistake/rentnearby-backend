using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Core.Models.Owner.Quote;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Infrastructure.Services.Owner.Quote;
using RentNearBy.Infrastructure.Services.Retention;
using RentNearBy.Infrastructure.Services.Retention.Owner;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Stays.Quotation;
using RentNearBy.Tests.Support.Owner;

namespace RentNearBy.Tests.Roles;

// S11 / Q19: leaving the Stay Owner role deletes the quotation master (bank blob included) inside the role purge and the quote
// images after the commit; counters and issue rows stay (numbers only). Account deletion: the four tables cascade with Users.
// Retention: issue rows go 180 days after the first issue, by the IssuedAt index, in the owner slot.
public class QuoteRoleChangeCleanupTests(TestDatabase db) : IntegrationTestBase(db)
{
    [Fact]
    public void Role_purge_sql_deletes_the_owners_master_only_for_the_stay_kind()
    {
        Assert.Equal("DELETE FROM \"StayQuoteProfiles\" WHERE \"UserId\" = @user", ListingPurgeService.QuoteProfileSql.Trim());
        var src = File.ReadAllText(Path.Combine(RentNearBy.Tests.Trips.CompletionCounterGuardTests.Root(),
            "RentNearBy.Infrastructure", "Services", "ListingPurgeService.cs"));
        Assert.Contains("if (listingKind == ListingKinds.Stay)", src);
    }

    [Fact]
    public void Both_role_paths_delete_the_quote_images_after_their_commit()
    {
        var root = RentNearBy.Tests.Trips.CompletionCounterGuardTests.Root();
        foreach (var file in new[] { "RoleHandlers.cs", "AdminRoleHandlers.cs" })
        {
            var src = File.ReadAllText(Path.Combine(root, "RentNearBy.Api", "Handlers", file));
            var cleanup = src.IndexOf("DeleteImagesBestEffortAsync(", StringComparison.Ordinal);
            Assert.True(cleanup > 0, file);
            Assert.True(cleanup > src.LastIndexOf("CommitTransactionAsync()", cleanup, StringComparison.Ordinal), file);
        }
    }

    [Fact]
    public async Task Image_cleanup_is_best_effort()
    {
        var store = new RecordingQuoteImageStore();
        var user = Guid.NewGuid();
        await new StayQuoteRoleCleanup(store, NullLogger<StayQuoteRoleCleanup>.Instance).DeleteImagesBestEffortAsync(user);
        Assert.Equal(new[] { user }, store.PrefixDeleted);
        await new StayQuoteRoleCleanup(new ThrowingStore(), NullLogger<StayQuoteRoleCleanup>.Instance).DeleteImagesBestEffortAsync(user);
    }

    private sealed class ThrowingStore : IQuoteImageStore
    {
        public Task<string> SaveQuoteImageAsync(Stream input, Guid userId, QuoteImageKind kind) => throw new InvalidOperationException();
        public Task<byte[]?> ReadQuoteImageAsync(string publicId, CancellationToken ct) => throw new InvalidOperationException();
        public Task DeleteQuoteImageAsync(string publicId) => throw new InvalidOperationException();
        public Task DeleteAllQuoteImagesAsync(Guid userId) => throw new InvalidOperationException("cloudinary down");
    }

    [IntegrationFact]
    public async Task Leaving_the_stay_role_deletes_the_master_and_keeps_the_numbers()
    {
        var user = await QuoteKit.ReadyOwnerAsync(CreateContext);
        await using (var c = CreateContext())
        {
            c.StayQuoteProfiles.Add(new StayQuoteProfile { UserId = user, Slogan = "s", BankEnc = "v1.x.y", HasBank = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            await c.SaveChangesAsync();
            Assert.True((await QuoteKit.Reserve(c).ReserveAsync(user, Guid.NewGuid(), QuoteKinds.QuotationCode, default)).Ok);
        }
        await using (var c = CreateContext())
        {
            await using var tx = await c.Database.BeginTransactionAsync();
            var purge = new ListingPurgeService(c, new RecordingPhotoService(), new RentNearBy.Infrastructure.Services.Owner.NullOwnerListingPurge(), NullLogger<ListingPurgeService>.Instance);
            await purge.DeleteUserListingRowsAsync(user, ListingKinds.Stay);
            await tx.CommitAsync();
        }
        await using var check = CreateContext();
        Assert.False(await check.StayQuoteProfiles.AnyAsync(p => p.UserId == user));
        Assert.True(await check.StayQuoteCounters.AnyAsync(p => p.UserId == user));
        Assert.True(await check.StayQuoteIssues.AnyAsync(p => p.UserId == user));
    }

    [IntegrationFact]
    public async Task Leaving_the_taxi_role_does_not_touch_a_stay_master()
    {
        Guid user;
        await using (var c = CreateContext())
        {
            user = (await OwnerSeed.TaxiDriverAsync(c)).UserId;
            c.StayQuoteProfiles.Add(new StayQuoteProfile { UserId = user, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            await c.SaveChangesAsync();
        }
        await using (var c = CreateContext())
        {
            var purge = new ListingPurgeService(c, new RecordingPhotoService(), new RentNearBy.Infrastructure.Services.Owner.NullOwnerListingPurge(), NullLogger<ListingPurgeService>.Instance);
            await purge.DeleteUserListingRowsAsync(user, ListingKinds.Taxi);
        }
        await using var check = CreateContext();
        Assert.True(await check.StayQuoteProfiles.AnyAsync(p => p.UserId == user));
    }

    [IntegrationFact]
    public async Task Deleting_the_user_row_cascades_all_four_tables()
    {
        var user = await QuoteKit.ReadyOwnerAsync(CreateContext);
        await using (var c = CreateContext())
        {
            c.StayQuoteProfiles.Add(new StayQuoteProfile { UserId = user, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            await c.SaveChangesAsync();
            Assert.True((await QuoteKit.Reserve(c).ReserveAsync(user, Guid.NewGuid(), QuoteKinds.QuotationCode, default)).Ok);
            await c.StayListings.Where(s => s.UserId == user).ExecuteDeleteAsync();
            await c.Users.Where(u => u.Id == user).ExecuteDeleteAsync();
        }
        await using var check = CreateContext();
        Assert.False(await check.StayQuoteProfiles.AnyAsync(p => p.UserId == user));
        Assert.False(await check.StayQuoteTermsAcceptances.AnyAsync(p => p.UserId == user));
        Assert.False(await check.StayQuoteCounters.AnyAsync(p => p.UserId == user));
        Assert.False(await check.StayQuoteIssues.AnyAsync(p => p.UserId == user));
    }

    [Fact]
    public void Coverage_lists_the_four_tables_as_cascade()
    {
        var c = AccountDeletionCoverage.Tables.ToDictionary(t => t.Table);
        foreach (var table in new[] { "StayQuoteProfiles", "StayQuoteTermsAcceptances", "StayQuoteCounters", "StayQuoteIssues" })
            Assert.True(c[table].Mode.HasFlag(DeletionModes.Cascade), table);
    }

    [Fact]
    public void Issue_purge_is_180_days_by_issued_at_with_the_whole_key()
    {
        var def = Assert.Single(StayQuoteRetentionRules.Definitions);
        Assert.Equal(("StayQuoteIssues", "StayQuoteIssues", 180, "IssuedAt"), (def.Name, def.Table, def.DefaultDays, def.OrderBy));
        Assert.Equal(RetentionGroups.OwnerCalendar, def.Group);
        Assert.Equal("t.\"IssuedAt\" < @cutoff", def.EligibleSql);
        var sql = new DeclarativeRetentionRule(def).BuildDeleteSql();
        Assert.Contains("(t.\"UserId\", t.\"DocumentId\", t.\"Kind\") IN", sql);
        Assert.Contains("FOR UPDATE SKIP LOCKED", sql);
        Assert.False(def.GuardExempt);
        Assert.DoesNotContain(OwnerRetentionRules.Definitions, d => d.Table == "StayQuoteIssues");
    }
}
