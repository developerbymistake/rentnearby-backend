using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Models.Owner.Quote;

namespace RentNearBy.Infrastructure.Data;

// Stay quotation (ARCHITECTURE 2): four tables keyed by the owner's UserId, all ON DELETE CASCADE from Users.
// No guest data, no document content. Counter and issue rows are updated in place without touching an index (fillfactor 90 = HOT).
public static class StayQuoteFeatureModel
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StayQuoteProfile>(e =>
        {
            e.ToTable("StayQuoteProfiles", t =>
            {
                t.HasCheckConstraint("ck_stayquoteprofiles_slogan", $"char_length(\"Slogan\") <= {QuoteRules.SloganMax}");
                t.HasCheckConstraint("ck_stayquoteprofiles_address", $"char_length(\"Address\") <= {QuoteRules.AddressMax}");
                t.HasCheckConstraint("ck_stayquoteprofiles_phone", $"char_length(\"Phone\") <= {QuoteRules.PhoneMax}");
                t.HasCheckConstraint("ck_stayquoteprofiles_email", $"char_length(\"Email\") <= {QuoteRules.EmailMax}");
                t.HasCheckConstraint("ck_stayquoteprofiles_gstin", $"\"Gstin\" IS NULL OR char_length(\"Gstin\") = {QuoteRules.GstinLength}");
                t.HasCheckConstraint("ck_stayquoteprofiles_terms", $"char_length(\"TermsText\") <= {QuoteRules.TermsMax}");
                t.HasCheckConstraint("ck_stayquoteprofiles_cancellation", $"char_length(\"CancellationText\") <= {QuoteRules.CancellationMax}");
                t.HasCheckConstraint("ck_stayquoteprofiles_bank", "\"HasBank\" = (\"BankEnc\" IS NOT NULL)");
                t.HasCheckConstraint("ck_stayquoteprofiles_assets", "\"AssetsVersion\" >= 0");
            });
            e.HasKey(x => x.UserId);
            e.Property(x => x.UserId).ValueGeneratedNever();
            e.Property(x => x.Slogan).HasMaxLength(QuoteRules.SloganMax);
            e.Property(x => x.Address).HasMaxLength(QuoteRules.AddressMax);
            e.Property(x => x.Phone).HasMaxLength(QuoteRules.PhoneMax);
            e.Property(x => x.Email).HasMaxLength(QuoteRules.EmailMax);
            e.Property(x => x.Gstin).HasMaxLength(QuoteRules.GstinLength);
            e.Property(x => x.BankEnc).HasColumnType("text");
            e.Property(x => x.HasBank).HasDefaultValue(false);
            e.Property(x => x.TermsText).HasMaxLength(QuoteRules.TermsMax);
            e.Property(x => x.CancellationText).HasMaxLength(QuoteRules.CancellationMax);
            e.Property(x => x.LogoPath).HasMaxLength(QuoteRules.ImagePathMax);
            e.Property(x => x.SignaturePath).HasMaxLength(QuoteRules.ImagePathMax);
            e.Property(x => x.AssetsVersion).HasDefaultValue(0);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
            e.HasOne<User>().WithOne().HasForeignKey<StayQuoteProfile>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<StayQuoteTermsAcceptance>(e =>
        {
            e.ToTable("StayQuoteTermsAcceptances", t => t.HasCheckConstraint("ck_stayquotetermsacceptances_version", "\"Version\" >= 1"));
            e.HasKey(x => new { x.UserId, x.Version });
            e.Property(x => x.AcceptedAt).HasDefaultValueSql("now()");
            e.Property(x => x.AppVersion).HasMaxLength(QuoteRules.AppVersionMax);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<StayQuoteCounter>(e =>
        {
            e.ToTable("StayQuoteCounters", t =>
            {
                t.HasCheckConstraint("ck_stayquotecounters_used", "\"Used\" >= 0");
                t.HasCheckConstraint("ck_stayquotecounters_quote", "\"NextQuoteNo\" >= 1");
                t.HasCheckConstraint("ck_stayquotecounters_proforma", "\"NextProformaNo\" >= 1");
            });
            e.HasKey(x => x.UserId);
            e.Property(x => x.UserId).ValueGeneratedNever();
            e.Property(x => x.NextQuoteNo).HasDefaultValue(1);
            e.Property(x => x.NextProformaNo).HasDefaultValue(1);
            e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
            e.HasAnnotation("Npgsql:StorageParameter:fillfactor", 90);
            e.HasOne<User>().WithOne().HasForeignKey<StayQuoteCounter>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<StayQuoteIssue>(e =>
        {
            e.ToTable("StayQuoteIssues", t =>
            {
                t.HasCheckConstraint("ck_stayquoteissues_kind", $"\"Kind\" IN ({QuoteKinds.QuotationCode}, {QuoteKinds.ProformaCode})");
                t.HasCheckConstraint("ck_stayquoteissues_number", "\"Number\" >= 1");
                t.HasCheckConstraint("ck_stayquoteissues_counts", "\"Downloads\" >= 1 AND \"FreeUses\" >= 0");
            });
            e.HasKey(x => new { x.UserId, x.DocumentId, x.Kind });
            e.Property(x => x.FreeUses).HasDefaultValue((short)0);
            e.HasIndex(x => new { x.UserId, x.Kind, x.Number }, "ux_stayquoteissues_number").IsUnique();
            e.HasIndex(x => x.IssuedAt, "ix_stayquoteissues_issuedat");
            e.HasAnnotation("Npgsql:StorageParameter:fillfactor", 90);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
