using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Tests.Stays.Quotation;

// ARCHITECTURE 2: the four quotation tables in the EF model (keys, unique number, purge index, checks, fillfactor, cascade), and
// in the ONE fresh InitialCreate the main session regenerates (this last test is red until that regeneration, by design).
public class QuoteModelTests
{
    private static ApplicationDbContext NewContext() => new(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseNpgsql("Host=localhost;Database=design_only", o => o.UseNetTopologySuite())
        .Options);

    private static IModel Model()
    {
        using var db = NewContext();
        return db.GetService<IDesignTimeModel>().Model;
    }

    private static IEntityType Table(string name) => Model().GetEntityTypes().Single(e => e.GetTableName() == name);

    [Fact]
    public void Profiles_are_one_row_per_owner_with_capped_text_and_no_name_or_url_column()
    {
        var t = Table("StayQuoteProfiles");
        Assert.Equal(["UserId"], t.FindPrimaryKey()!.Properties.Select(p => p.Name));
        var columns = t.GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain(columns, c => c.Contains("Name", StringComparison.Ordinal) || c.Contains("Url", StringComparison.Ordinal));
        Assert.Equal(80, t.FindProperty("Slogan")!.GetMaxLength());
        Assert.Equal(1200, t.FindProperty("TermsText")!.GetMaxLength());
        Assert.Contains(t.GetCheckConstraints(), c => c.Name == "ck_stayquoteprofiles_bank");
        Assert.Contains(t.GetForeignKeys(), f => f.PrincipalEntityType.ClrType == typeof(User) && f.DeleteBehavior == DeleteBehavior.Cascade);
    }

    [Fact]
    public void Issues_key_by_owner_document_kind_with_a_unique_number_and_a_purge_index()
    {
        var t = Table("StayQuoteIssues");
        Assert.Equal(["UserId", "DocumentId", "Kind"], t.FindPrimaryKey()!.Properties.Select(p => p.Name));
        var number = t.GetIndexes().Single(i => i.GetDatabaseName() == "ux_stayquoteissues_number");
        Assert.True(number.IsUnique);
        Assert.Equal(["UserId", "Kind", "Number"], number.Properties.Select(p => p.Name));
        Assert.Contains(t.GetIndexes(), i => i.GetDatabaseName() == "ix_stayquoteissues_issuedat");
        Assert.Equal(90, t.FindAnnotation("Npgsql:StorageParameter:fillfactor")!.Value);
        Assert.DoesNotContain(t.GetProperties(), p => p.Name.Contains("Guest", StringComparison.Ordinal));
    }

    [Fact]
    public void Counters_are_the_lock_row_with_checks_and_fillfactor()
    {
        var t = Table("StayQuoteCounters");
        Assert.Equal(["UserId"], t.FindPrimaryKey()!.Properties.Select(p => p.Name));
        Assert.Equal(90, t.FindAnnotation("Npgsql:StorageParameter:fillfactor")!.Value);
        Assert.Equal(["ck_stayquotecounters_proforma", "ck_stayquotecounters_quote", "ck_stayquotecounters_used"],
            t.GetCheckConstraints().Select(c => c.Name!).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void Terms_acceptances_keep_one_row_per_version()
        => Assert.Equal(["UserId", "Version"], Table("StayQuoteTermsAcceptances").FindPrimaryKey()!.Properties.Select(p => p.Name));

    // Red until the main session deletes the old migrations and regenerates ONE InitialCreate (lanes never write a migration).
    [Fact]
    public void The_fresh_initial_create_carries_the_four_tables()
    {
        using var db = NewContext();
        var snapshot = db.GetService<IMigrationsAssembly>().ModelSnapshot!.Model;
        var tables = snapshot.GetEntityTypes().Select(e => e.GetTableName()).ToHashSet();
        foreach (var table in new[] { "StayQuoteProfiles", "StayQuoteTermsAcceptances", "StayQuoteCounters", "StayQuoteIssues" })
            Assert.Contains(table, tables);
    }
}
