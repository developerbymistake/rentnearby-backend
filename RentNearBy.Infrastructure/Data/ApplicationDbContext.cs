using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Models;

namespace RentNearBy.Infrastructure.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : DbContext(options)
{
    public DbSet<User> Users { get; set; }
    public DbSet<Session> Sessions { get; set; }
    public DbSet<Admin> Admins { get; set; }
    public DbSet<AdminSession> AdminSessions { get; set; }
    public DbSet<RoomListing> RoomListings { get; set; }
    public DbSet<RoomPhoto> RoomPhotos { get; set; }
    public DbSet<District> Districts { get; set; }
    public DbSet<City> Cities { get; set; }
    public DbSet<RoomType> RoomTypes { get; set; }
    public DbSet<CoinFeature> CoinFeatures { get; set; }
    public DbSet<CoinPlan> CoinPlans { get; set; }
    public DbSet<PlotType> PlotTypes { get; set; }
    public DbSet<PlotListing> PlotListings { get; set; }
    public DbSet<PlotPhoto> PlotPhotos { get; set; }
    public DbSet<DeviceToken> DeviceTokens { get; set; }
    public DbSet<NotificationLog> NotificationLogs { get; set; }
    public DbSet<DistrictBanner> DistrictBanners { get; set; }
    public DbSet<BannerDismissal> BannerDismissals { get; set; }
    public DbSet<ReportReason> ReportReasons { get; set; }
    public DbSet<ListingReport> ListingReports { get; set; }
    public DbSet<AdminDeviceToken> AdminDeviceTokens { get; set; }
    public DbSet<Conversation> Conversations { get; set; }
    public DbSet<Message> Messages { get; set; }
    public DbSet<UserBlock> UserBlocks { get; set; }
    public DbSet<QuestionTemplate> QuestionTemplates { get; set; }
    public DbSet<Wallet> Wallets { get; set; }
    public DbSet<CoinTransaction> CoinTransactions { get; set; }
    public DbSet<CoinPack> CoinPacks { get; set; }
    public DbSet<ListingLimitSetting> ListingLimitSettings { get; set; }
    public DbSet<Coupon> Coupons { get; set; }
    public DbSet<CouponRedemption> CouponRedemptions { get; set; }
    public DbSet<CoinPackPurchase> CoinPackPurchases { get; set; }
    public DbSet<ServiceSection> ServiceSections { get; set; }
    public DbSet<ServiceCategory> ServiceCategories { get; set; }
    public DbSet<Service> Services { get; set; }
    public DbSet<ServicePackage> ServicePackages { get; set; }
    public DbSet<Inclusion> Inclusions { get; set; }
    public DbSet<PackageInclusion> PackageInclusions { get; set; }
    public DbSet<Agent> Agents { get; set; }
    public DbSet<AgentServiceCategory> AgentServiceCategories { get; set; }
    public DbSet<Inquiry> Inquiries { get; set; }
    public DbSet<InquiryStatusHistory> InquiryStatusHistories { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Admin>(e =>
        {
            e.HasKey(a => a.Id);
            e.Property(a => a.Id).HasDefaultValueSql("gen_random_uuid()");
            e.HasIndex(a => a.PhoneNumber).IsUnique();
            e.Property(a => a.CreatedAt).HasDefaultValueSql("now()");
            e.Property(a => a.UpdatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<AdminSession>(e =>
        {
            e.HasKey(s => s.Id);
            e.Property(s => s.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(s => s.CreatedAt).HasDefaultValueSql("now()");
            e.HasOne(s => s.Admin)
             .WithMany(a => a.Sessions)
             .HasForeignKey(s => s.AdminId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(s => s.AdminId);
            e.HasIndex(s => s.ExpiresAt);
        });

        modelBuilder.Entity<User>(e =>
        {
            e.HasKey(u => u.Id);
            e.Property(u => u.Id).HasDefaultValueSql("gen_random_uuid()");
            e.HasIndex(u => u.PhoneNumber).IsUnique();
            e.Property(u => u.CreatedAt).HasDefaultValueSql("now()");
            e.Property(u => u.UpdatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<Session>(e =>
        {
            e.HasKey(s => s.Id);
            e.Property(s => s.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(s => s.CreatedAt).HasDefaultValueSql("now()");
            e.HasIndex(s => s.UserId);
            e.HasIndex(s => s.ExpiresAt);
            e.HasOne(s => s.User)
             .WithMany()
             .HasForeignKey(s => s.UserId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<District>(e =>
        {
            e.HasKey(d => d.Id);
            e.Property(d => d.Id).HasDefaultValueSql("gen_random_uuid()");
            e.HasIndex(d => new { d.StateName, d.Name }).IsUnique(); // district name unique per state (same name can exist in different states)
            e.Property(d => d.CreatedAt).HasDefaultValueSql("now()");
            e.Property(d => d.StateName).IsRequired();
            e.Property(d => d.IsActive).HasDefaultValue(false);
            e.HasIndex(d => d.IsActive);
            e.HasIndex(d => d.StateName);
            e.Property(d => d.Boundary).HasColumnType("geometry(Geometry, 4326)");
            // Partial spatial index — only active districts in the GiST tree.
            // GetContext does: WHERE IsActive = true AND ST_Contains(Boundary, point)
            // Smaller index (active-only) means faster R-tree traversal than indexing all 700.
            e.HasIndex(d => d.Boundary)
             .HasMethod("gist")
             .HasDatabaseName("ix_districts_boundary_active_gist")
             .HasFilter("\"IsActive\" = true");
        });

        modelBuilder.Entity<City>(e =>
        {
            e.HasKey(c => c.Id);
            e.Property(c => c.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(c => c.CreatedAt).HasDefaultValueSql("now()");
            e.HasIndex(c => c.DistrictId);
            e.HasIndex(c => new { c.DistrictId, c.Name }).IsUnique();
            e.HasOne(c => c.District)
             .WithMany(d => d.Cities)
             .HasForeignKey(c => c.DistrictId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RoomType>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).HasDefaultValueSql("gen_random_uuid()");
            e.HasIndex(r => r.Name).IsUnique();
            e.Property(r => r.CreatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<PlotType>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.Id).HasDefaultValueSql("gen_random_uuid()");
            e.HasIndex(p => p.Name).IsUnique();
            e.Property(p => p.CreatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<ReportReason>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).HasDefaultValueSql("gen_random_uuid()");
            e.HasIndex(r => r.Name).IsUnique();
            e.Property(r => r.CreatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<ListingReport>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(r => r.CreatedAt).HasDefaultValueSql("now()");
            e.Property(r => r.Status).HasDefaultValue("Pending");

            e.HasOne(r => r.Reason)
             .WithMany(rr => rr.Reports)
             .HasForeignKey(r => r.ReasonId)
             .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(r => r.ResolvedByAdmin)
             .WithMany()
             .HasForeignKey(r => r.ResolvedByAdminId)
             .OnDelete(DeleteBehavior.SetNull);

            // Used by the auto-resolve hook and the first-pending-report check
            e.HasIndex(r => new { r.ListingId, r.ListingType, r.Status });
            // Used by the admin tab filter (?status=Pending|Reviewed|All)
            e.HasIndex(r => r.Status);

            // Stops the same user filing >1 simultaneous Pending report on the same listing.
            // Partial unique index — race-safe under concurrent submits, unlike an app-level
            // check-then-insert which has a TOCTOU window.
            e.HasIndex(r => new { r.ReporterUserId, r.ListingId })
             .IsUnique()
             .HasDatabaseName("ix_listingreports_reporter_listing_pending")
             .HasFilter("\"Status\" = 'Pending'");
        });

        modelBuilder.Entity<Conversation>(e =>
        {
            e.HasKey(c => c.Id);
            e.Property(c => c.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(c => c.CreatedAt).HasDefaultValueSql("now()");
            e.Property(c => c.Status).HasDefaultValue("Active");
            e.Property(c => c.UnreadCountForRenter).HasDefaultValue(0);
            e.Property(c => c.UnreadCountForOwner).HasDefaultValue(0);

            // Powers the Chats-list query for both sides of a conversation.
            e.HasIndex(c => new { c.RenterId, c.LastMessageAt });
            e.HasIndex(c => new { c.OwnerId, c.LastMessageAt });

            // Race-safe under concurrent create attempts (double-tap, retry-on-timeout) —
            // the find-or-create check in CreateConversation is check-then-act at the app
            // level, so the DB constraint is what actually prevents a duplicate thread.
            e.HasIndex(c => new { c.RenterId, c.OwnerId, c.ListingType, c.ListingId })
             .IsUnique()
             .HasDatabaseName("ix_conversations_renter_owner_listing");
        });

        modelBuilder.Entity<Message>(e =>
        {
            e.HasKey(m => m.Id);
            e.Property(m => m.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(m => m.CreatedAt).HasDefaultValueSql("now()");
            e.Property(m => m.PayloadJson).HasColumnType("jsonb");

            e.HasOne(m => m.Conversation)
             .WithMany(c => c.Messages)
             .HasForeignKey(m => m.ConversationId)
             .OnDelete(DeleteBehavior.Cascade);

            // Powers cursor-based history pagination.
            e.HasIndex(m => new { m.ConversationId, m.CreatedAt });

            // Powers MarkReadBulkAsync's WHERE ConversationId=@id AND SenderId!=@me AND
            // ReadAt IS NULL — previously only benefited from the (ConversationId, CreatedAt)
            // index's leading column, then scanned the rest. Fires on every conversation-open
            // and every incoming message, so it's a hot path.
            e.HasIndex(m => new { m.ConversationId, m.SenderId, m.ReadAt });

            // Self-referencing: an answer points back at the question message it answers.
            e.HasOne<Message>()
             .WithMany()
             .HasForeignKey(m => m.RespondsToMessageId)
             .OnDelete(DeleteBehavior.Restrict);

            // At most one answer per question, enforced at the DB level (partial so it
            // never applies to the vast majority of messages that don't answer anything).
            e.HasIndex(m => m.RespondsToMessageId)
             .IsUnique()
             .HasDatabaseName("ix_messages_responds_to_unique")
             .HasFilter("\"RespondsToMessageId\" IS NOT NULL");
        });

        modelBuilder.Entity<UserBlock>(e =>
        {
            e.HasKey(b => b.Id);
            e.Property(b => b.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(b => b.CreatedAt).HasDefaultValueSql("now()");

            e.HasIndex(b => new { b.BlockerId, b.BlockedId }).IsUnique();
        });

        modelBuilder.Entity<QuestionTemplate>(e =>
        {
            e.HasKey(q => q.Id);
            e.Property(q => q.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(q => q.CreatedAt).HasDefaultValueSql("now()");
            e.Property(q => q.AnswerOptionsJson).HasColumnType("jsonb");
            e.Property(q => q.IsActive).HasDefaultValue(true);

            e.HasIndex(q => q.Key).IsUnique();

            e.HasOne<RoomType>()
             .WithMany()
             .HasForeignKey(q => q.RoomTypeId)
             .OnDelete(DeleteBehavior.Restrict);
            e.HasOne<PlotType>()
             .WithMany()
             .HasForeignKey(q => q.PlotTypeId)
             .OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(q => q.RoomTypeId);
            e.HasIndex(q => q.PlotTypeId);
        });

        modelBuilder.Entity<CoinFeature>(e =>
        {
            e.HasKey(f => f.Id);
            e.Property(f => f.Id).HasDefaultValueSql("gen_random_uuid()");
            e.HasIndex(f => f.Key).IsUnique();
            e.Property(f => f.CreatedAt).HasDefaultValueSql("now()");
            e.Property(f => f.UpdatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<CoinPlan>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.Id).HasDefaultValueSql("gen_random_uuid()");
            // Unique per feature, not globally — Room and Plot (and any future coin-gated feature)
            // each need their own "BASIC"/"STANDARD"/"PREMIUM".
            e.HasIndex(p => new { p.FeatureKey, p.PlanType }).IsUnique();
            e.Property(p => p.CreatedAt).HasDefaultValueSql("now()");
            e.Property(p => p.UpdatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<RoomListing>(e =>
        {
            e.HasKey(l => l.Id);
            e.Property(l => l.Id).HasDefaultValueSql("gen_random_uuid()");
            // Postgres system column, no migration needed — a shadow uint property configured as a
            // row-version is what this Npgsql EF Core version's NpgsqlPostgresModelFinalizingConvention
            // maps to the xmin system column automatically (the older .UseXminAsConcurrencyToken()
            // helper doesn't exist in this package version). Makes GoLiveHandlers.GoLiveRoom's
            // DbUpdateConcurrencyException catch meaningful: two concurrent Go-Live attempts on the
            // same listing can no longer both silently win.
            e.Property<uint>("xmin").IsRowVersion();
            e.Property(l => l.CreatedAt).HasDefaultValueSql("now()");
            e.Property(l => l.UpdatedAt).HasDefaultValueSql("now()");

            e.HasOne(l => l.User)
             .WithMany()
             .HasForeignKey(l => l.UserId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(l => l.RoomType)
             .WithMany()
             .HasForeignKey(l => l.RoomTypeId)
             .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(l => l.District)
             .WithMany()
             .HasForeignKey(l => l.DistrictId)
             .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(l => l.City)
             .WithMany()
             .HasForeignKey(l => l.CityId)
             .IsRequired(false)
             .OnDelete(DeleteBehavior.SetNull);

            // FK indexes (explicit — cascade + join performance)
            e.HasIndex(l => l.DistrictId);
            e.HasIndex(l => l.CityId);
            e.HasIndex(l => l.UserId);
            e.HasIndex(l => l.RoomTypeId);
            // Filter indexes
            e.HasIndex(l => l.IsActive);
            e.HasIndex(l => l.PriceMonthly);
            e.HasIndex(l => l.CreatedAt);
            // Composite: DeleteDistrict guard — AnyAsync(l.DistrictId == id && l.IsActive)
            e.HasIndex(l => new { l.DistrictId, l.IsActive });
            // Stored geography column — auto-computed from Latitude/Longitude by PostgreSQL
            e.Property<NetTopologySuite.Geometries.Point?>("Location")
             .HasColumnType("geography(Point, 4326)")
             .HasComputedColumnSql(
                 "ST_SetSRID(ST_MakePoint(\"Longitude\"::float8, \"Latitude\"::float8), 4326)::geography",
                 stored: true);
            e.HasIndex("Location")
             .HasMethod("gist")
             .HasDatabaseName("ix_listings_location_gist");
            // Composite: My RoomListings pagination — filter by user, sort newest first
            e.HasIndex(l => new { l.UserId, l.CreatedAt });
            // Composite: admin count queries — COUNT(UserId = x AND !IsDeleted)
            e.HasIndex(l => new { l.UserId, l.IsDeleted });
            // Composite: GetNearby secondary filter — city + active (GiST is primary spatial filter)
            e.HasIndex(l => new { l.CityId, l.IsActive });
            // Composite: city-based active listings newest first (replaces district composite)
            e.HasIndex(l => new { l.CityId, l.IsActive, l.CreatedAt });
            // Composite: search with room type filter
            e.HasIndex(l => new { l.IsActive, l.RoomTypeId });
            // Partial: district digest job — find rooms not yet included in a daily digest.
            // Filtered on DigestNotifiedAt IS NULL so the index stays small regardless of
            // total historical row count (only ever contains not-yet-digested rows).
            e.HasIndex(l => new { l.IsActive, l.IsDeleted, l.DigestNotifiedAt, l.DistrictId })
             .HasDatabaseName("ix_roomlistings_digest_pending")
             .HasFilter("\"DigestNotifiedAt\" IS NULL");
        });

        modelBuilder.Entity<RoomPhoto>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(p => p.UploadedAt).HasDefaultValueSql("now()");
            e.HasOne(p => p.RoomListing)
             .WithMany(l => l.Photos)
             .HasForeignKey(p => p.RoomListingId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(p => p.RoomListingId);
        });

        modelBuilder.Entity<Wallet>(e =>
        {
            e.HasKey(w => w.UserId);
            e.HasOne(w => w.User)
             .WithOne(u => u.Wallet)
             .HasForeignKey<Wallet>(w => w.UserId)
             .OnDelete(DeleteBehavior.Cascade);
            e.Property(w => w.CreatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<CoinTransaction>(e =>
        {
            e.HasKey(t => t.Id);
            e.Property(t => t.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(t => t.CreatedAt).HasDefaultValueSql("now()");

            e.HasOne(t => t.User)
             .WithMany()
             .HasForeignKey(t => t.UserId)
             .OnDelete(DeleteBehavior.Cascade);

            // Separate, optional FK to the admin who performed a manual credit/debit — must not
            // cascade-delete a user's whole ledger if that admin account is ever removed.
            e.HasOne(t => t.PerformedByUser)
             .WithMany()
             .HasForeignKey(t => t.PerformedByUserId)
             .IsRequired(false)
             .OnDelete(DeleteBehavior.SetNull);

            // "My ledger, newest first, paginated" — same shape as RoomListing/PlotListing/
            // Conversation's existing per-user list indexes.
            e.HasIndex(t => new { t.UserId, t.CreatedAt });
            // Admin-wide ledger view filters by Reason, sorted by date — same shape as today's
            // admin-transactions status-filter + date-sort query.
            e.HasIndex(t => new { t.Reason, t.CreatedAt });

            // Exactly-once guard for reasons where the same (UserId, Reason, ReferenceId) must never
            // apply twice — a retried recharge webhook, coupon redemption, welcome-bonus hook, or a
            // double-tapped admin credit/debit. Deliberately excludes ROOM_GOLIVE/PLOT_GOLIVE, which
            // legitimately reuse the same listing Guid as ReferenceId across renewals. The filter is
            // generated from CoinTransactionReasons.AllOneShotReasons, not hand-typed, so the C# list
            // and the SQL filter can never drift apart — same discipline as
            // ix_paymenttransactions_pending_room_upgrade's naming lesson above.
            var oneShotReasonList = string.Join(", ", CoinTransactionReasons.AllOneShotReasons.Select(r => $"'{r}'"));
            e.HasIndex(t => new { t.UserId, t.Reason, t.ReferenceId }, "ix_cointransactions_oneshot_unique")
             .IsUnique()
             .HasFilter($"\"Reason\" IN ({oneShotReasonList}) AND \"ReferenceId\" IS NOT NULL");
        });

        modelBuilder.Entity<CoinPack>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(p => p.CreatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<ListingLimitSetting>(e =>
        {
            e.HasKey(s => s.Id);
            e.Property(s => s.Id).HasDefaultValueSql("gen_random_uuid()");
            e.HasIndex(s => s.ListingKind).IsUnique();
        });

        modelBuilder.Entity<Coupon>(e =>
        {
            e.HasKey(c => c.Id);
            e.Property(c => c.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(c => c.CreatedAt).HasDefaultValueSql("now()");

            // Most rows have a null Code (welcome-bonus/future non-typed triggers) — a plain unique
            // index would treat every one of those nulls as a separate value anyway (Postgres unique
            // indexes already ignore NULLs), but the filter documents the intent explicitly, same as
            // Messages.RespondsToMessageId's partial-unique pattern.
            e.HasIndex(c => c.Code)
             .IsUnique()
             .HasDatabaseName("ix_coupons_code_unique")
             .HasFilter("\"Code\" IS NOT NULL");
            e.HasIndex(c => c.Status);
            e.HasIndex(c => c.TriggerType);

            // PerUserLimit > 1 (real multi-use-per-user coupons) isn't implemented in v1 — hard-enforced
            // here so a bug can't silently create one; see design doc §7 Open Flags.
            e.ToTable(t => t.HasCheckConstraint("ck_coupons_peruserlimit_one", "\"PerUserLimit\" = 1"));
        });

        modelBuilder.Entity<CouponRedemption>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).HasDefaultValueSql("gen_random_uuid()");

            e.HasOne(r => r.Coupon)
             .WithMany()
             .HasForeignKey(r => r.CouponId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(r => r.User)
             .WithMany()
             .HasForeignKey(r => r.UserId)
             .OnDelete(DeleteBehavior.Cascade);

            // The actual double-claim guard — closes the TOCTOU window the app-level status/limit
            // check in CouponService can't prevent on its own, same pattern as PaymentTransaction's
            // pending-order indexes and Conversation's renter/owner/listing unique index.
            e.HasIndex(r => new { r.CouponId, r.UserId })
             .IsUnique()
             .HasDatabaseName("ix_couponredemptions_coupon_user_unique");
        });

        modelBuilder.Entity<CoinPackPurchase>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(p => p.CreatedAt).HasDefaultValueSql("now()");

            e.HasOne<User>()
             .WithMany()
             .HasForeignKey(p => p.UserId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne<CoinPack>()
             .WithMany()
             .HasForeignKey(p => p.CoinPackId)
             .OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(p => p.RazorpayOrderId);

            // Two DISTINCT indexes on the same UserId column — named explicitly on both HasIndex()
            // calls. Calling HasIndex(p => p.UserId) unnamed a second time here would silently
            // collide with the first (EF Core resolves repeated HasIndex() calls on the identical
            // property list to the same underlying index object, last-writer-wins) — the exact bug
            // this codebase's own PaymentTransaction config already warns about.
            e.HasIndex(p => p.UserId, "ix_coinpackpurchases_user"); // general "all purchases for this user" lookups
            e.HasIndex(p => p.UserId, "ix_coinpackpurchases_pending_user") // race-safety guard, not a lookup index
             .IsUnique()
             .HasFilter("\"Status\" = 'PENDING'");
        });

        modelBuilder.Entity<PlotListing>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property<uint>("xmin").IsRowVersion(); // see the matching comment on RoomListing above
            e.Property(p => p.CreatedAt).HasDefaultValueSql("now()");
            e.Property(p => p.UpdatedAt).HasDefaultValueSql("now()");

            e.HasOne(p => p.PlotType)
             .WithMany(t => t.PlotListings)
             .HasForeignKey(p => p.PlotTypeId)
             .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(p => p.User)
             .WithMany()
             .HasForeignKey(p => p.UserId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(p => p.District)
             .WithMany()
             .HasForeignKey(p => p.DistrictId)
             .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(p => p.City)
             .WithMany()
             .HasForeignKey(p => p.CityId)
             .IsRequired(false)
             .OnDelete(DeleteBehavior.SetNull);

            e.HasIndex(p => p.PlotTypeId);
            e.HasIndex(p => p.UserId);
            e.HasIndex(p => p.DistrictId);
            e.HasIndex(p => p.CityId);
            e.HasIndex(p => p.IsActive);
            e.HasIndex(p => p.CreatedAt);
            e.HasIndex(p => p.AreaSqft);
            // Composite: DeleteDistrict guard — AnyAsync(p.DistrictId == id && p.IsActive)
            e.HasIndex(p => new { p.DistrictId, p.IsActive });

            e.Property<NetTopologySuite.Geometries.Point?>("Location")
             .HasColumnType("geography(Point, 4326)")
             .HasComputedColumnSql(
                 "ST_SetSRID(ST_MakePoint(\"Longitude\"::float8, \"Latitude\"::float8), 4326)::geography",
                 stored: true);
            e.HasIndex("Location")
             .HasMethod("gist")
             .HasDatabaseName("ix_plots_location_gist");

            e.HasIndex(p => new { p.UserId, p.CreatedAt });
            e.HasIndex(p => new { p.CityId, p.IsActive });
            e.HasIndex(p => new { p.CityId, p.IsActive, p.CreatedAt });
            // Composite: admin count queries — COUNT(UserId = x AND !IsDeleted)
            e.HasIndex(p => new { p.UserId, p.IsDeleted });
            // Partial: district digest job — find plots not yet included in a daily digest.
            // Filtered on DigestNotifiedAt IS NULL so the index stays small regardless of
            // total historical row count (only ever contains not-yet-digested rows).
            e.HasIndex(p => new { p.IsActive, p.IsDeleted, p.DigestNotifiedAt, p.DistrictId })
             .HasDatabaseName("ix_plotlistings_digest_pending")
             .HasFilter("\"DigestNotifiedAt\" IS NULL");
        });

        modelBuilder.Entity<PlotPhoto>(e =>
        {
            e.HasKey(ph => ph.Id);
            e.Property(ph => ph.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(ph => ph.UploadedAt).HasDefaultValueSql("now()");
            e.HasOne(ph => ph.PlotListing)
             .WithMany(p => p.Photos)
             .HasForeignKey(ph => ph.PlotId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(ph => ph.PlotId);
        });

        modelBuilder.Entity<DeviceToken>(e =>
        {
            e.HasKey(d => d.Id);
            e.Property(d => d.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(d => d.CreatedAt).HasDefaultValueSql("now()");
            e.Property(d => d.UpdatedAt).HasDefaultValueSql("now()");
            e.HasOne(d => d.User)
             .WithMany()
             .HasForeignKey(d => d.UserId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(d => d.UserId);
            e.HasIndex(d => new { d.UserId, d.IsValid });
            e.HasIndex(d => d.Token);
        });

        modelBuilder.Entity<AdminDeviceToken>(e =>
        {
            e.HasKey(d => d.Id);
            e.Property(d => d.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(d => d.CreatedAt).HasDefaultValueSql("now()");
            e.Property(d => d.UpdatedAt).HasDefaultValueSql("now()");
            e.HasOne(d => d.Admin)
             .WithMany()
             .HasForeignKey(d => d.AdminId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(d => d.AdminId);
            e.HasIndex(d => new { d.AdminId, d.IsValid });
            e.HasIndex(d => d.Token);
            e.HasIndex(d => d.IsValid);
        });

        modelBuilder.Entity<NotificationLog>(e =>
        {
            e.HasKey(n => n.Id);
            e.Property(n => n.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(n => n.SentAt).HasDefaultValueSql("now()");
            e.HasOne(n => n.User)
             .WithMany()
             .HasForeignKey(n => n.UserId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(n => n.UserId);
            e.HasIndex(n => new { n.UserId, n.Type, n.SentAt });
        });

        modelBuilder.Entity<DistrictBanner>(e =>
        {
            e.HasKey(b => b.Id);
            e.Property(b => b.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(b => b.CreatedAt).HasDefaultValueSql("now()");
            e.HasIndex(b => b.DistrictId).IsUnique();
            e.HasOne(b => b.District)
             .WithMany()
             .HasForeignKey(b => b.DistrictId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BannerDismissal>(e =>
        {
            e.HasKey(d => new { d.UserId, d.BannerId });
            e.Property(d => d.DismissedAt).HasDefaultValueSql("now()");
            e.HasOne(d => d.Banner)
             .WithMany(b => b.Dismissals)
             .HasForeignKey(d => d.BannerId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(d => d.User)
             .WithMany()
             .HasForeignKey(d => d.UserId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ServiceSection>(e =>
        {
            e.HasKey(s => s.Id);
            e.Property(s => s.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(s => s.CreatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<ServiceCategory>(e =>
        {
            e.HasKey(c => c.Id);
            e.Property(c => c.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(c => c.CreatedAt).HasDefaultValueSql("now()");

            e.HasOne(c => c.ServiceSection)
             .WithMany(s => s.Categories)
             .HasForeignKey(c => c.ServiceSectionId)
             .OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(c => c.ServiceSectionId);
        });

        modelBuilder.Entity<Service>(e =>
        {
            e.HasKey(s => s.Id);
            e.Property(s => s.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(s => s.CreatedAt).HasDefaultValueSql("now()");
            e.Property(s => s.UpdatedAt).HasDefaultValueSql("now()");

            e.HasOne(s => s.ServiceCategory)
             .WithMany(c => c.Services)
             .HasForeignKey(s => s.ServiceCategoryId)
             .OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(s => s.ServiceCategoryId);
        });

        modelBuilder.Entity<ServicePackage>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(p => p.CreatedAt).HasDefaultValueSql("now()");
            e.Property(p => p.UpdatedAt).HasDefaultValueSql("now()");

            e.HasOne(p => p.Service)
             .WithMany(s => s.Packages)
             .HasForeignKey(p => p.ServiceId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(p => p.ServiceId);
        });

        modelBuilder.Entity<Inclusion>(e =>
        {
            e.HasKey(i => i.Id);
            e.Property(i => i.Id).HasDefaultValueSql("gen_random_uuid()");
        });

        modelBuilder.Entity<PackageInclusion>(e =>
        {
            e.HasKey(pi => new { pi.ServicePackageId, pi.InclusionId });
            e.HasOne(pi => pi.ServicePackage)
             .WithMany(p => p.PackageInclusions)
             .HasForeignKey(pi => pi.ServicePackageId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(pi => pi.Inclusion)
             .WithMany(i => i.PackageInclusions)
             .HasForeignKey(pi => pi.InclusionId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(pi => pi.InclusionId);
        });

        modelBuilder.Entity<Agent>(e =>
        {
            e.HasKey(a => a.Id);
            e.Property(a => a.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(a => a.CreatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<AgentServiceCategory>(e =>
        {
            e.HasKey(ac => new { ac.AgentId, ac.ServiceCategoryId });
            e.HasOne(ac => ac.Agent)
             .WithMany(a => a.AgentServiceCategories)
             .HasForeignKey(ac => ac.AgentId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(ac => ac.ServiceCategory)
             .WithMany(c => c.AgentServiceCategories)
             .HasForeignKey(ac => ac.ServiceCategoryId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(ac => ac.ServiceCategoryId);
        });

        modelBuilder.Entity<Inquiry>(e =>
        {
            e.HasKey(i => i.Id);
            e.Property(i => i.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(i => i.CreatedAt).HasDefaultValueSql("now()");
            e.Property(i => i.UpdatedAt).HasDefaultValueSql("now()");

            e.HasOne(i => i.User)
             .WithMany()
             .HasForeignKey(i => i.UserId)
             .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(i => i.Service)
             .WithMany()
             .HasForeignKey(i => i.ServiceId)
             .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(i => i.ServicePackage)
             .WithMany()
             .HasForeignKey(i => i.ServicePackageId)
             .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(i => i.AssignedAgent)
             .WithMany()
             .HasForeignKey(i => i.AssignedAgentId)
             .IsRequired(false)
             .OnDelete(DeleteBehavior.SetNull);

            e.HasIndex(i => i.UserId);
            e.HasIndex(i => i.ServiceId);
            e.HasIndex(i => i.ServicePackageId);
            e.HasIndex(i => i.AssignedAgentId);
            e.HasIndex(i => i.Status);
        });

        modelBuilder.Entity<InquiryStatusHistory>(e =>
        {
            e.HasKey(h => h.Id);
            e.Property(h => h.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(h => h.CreatedAt).HasDefaultValueSql("now()");

            e.HasOne(h => h.Inquiry)
             .WithMany(i => i.StatusHistory)
             .HasForeignKey(h => h.InquiryId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(h => h.ChangedByAdmin)
             .WithMany()
             .HasForeignKey(h => h.ChangedByAdminId)
             .IsRequired(false)
             .OnDelete(DeleteBehavior.SetNull);

            e.HasIndex(h => h.InquiryId);
            e.HasIndex(h => h.ChangedByAdminId);
        });

    }
}
