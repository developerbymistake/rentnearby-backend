using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Entities.Buddy;
using RentNearBy.Core.Entities.TaxiRequests;
using RentNearBy.Core.Models.Buddy;

namespace RentNearBy.Infrastructure.Data;

// Buddies, trip sharing and expenses (CONTRACT_BACKEND section 1.2). No DbSet properties: code uses Set<T>().
// Transitions are conditional updates; no xmin. Never a foreign key to or an update of Trips beyond ON DELETE CASCADE.
public static class BuddyFeatureModel
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BuddyRequest>(e =>
        {
            e.ToTable("BuddyRequests", t =>
            {
                t.HasCheckConstraint("ck_buddyrequests_status", $"\"Status\" IN ({BuddyRequestStatuses.SqlList})");
                t.HasCheckConstraint("ck_buddyrequests_pair", "\"PairLowId\" < \"PairHighId\"");
                t.HasCheckConstraint("ck_buddyrequests_not_self", "\"FromUserId\" <> \"ToUserId\"");
                t.HasCheckConstraint("ck_buddyrequests_source", $"\"Source\" IN ({BuddyRequestSources.SqlList})");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.Status).HasMaxLength(20).IsRequired();
            e.Property(x => x.Source).HasMaxLength(10).IsRequired();
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.Property(x => x.BlockedSender).HasDefaultValue(false);

            e.HasOne<User>().WithMany().HasForeignKey(x => x.FromUserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.ToUserId).OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => new { x.PairLowId, x.PairHighId }, "ux_buddyrequests_pair_open")
             .IsUnique()
             .HasFilter($"\"Status\" = '{BuddyRequestStatuses.Pending}'");
            e.HasIndex(x => new { x.FromUserId, x.ClientRequestId }, "ux_buddyrequests_from_client").IsUnique();
            e.HasIndex(x => new { x.ToUserId, x.CreatedAt, x.Id }, "ix_buddyrequests_to_pending")
             .IsDescending(false, true, true)
             .HasFilter($"\"Status\" = '{BuddyRequestStatuses.Pending}'");
            e.HasIndex(x => x.ToUserId, "ix_buddyrequests_to");
            e.HasIndex(x => new { x.FromUserId, x.CreatedAt, x.Id }, "ix_buddyrequests_from_created")
             .IsDescending(false, true, true);
            e.HasIndex(x => new { x.PairLowId, x.PairHighId, x.CreatedAt }, "ix_buddyrequests_pair_created")
             .IsDescending(false, false, true);
            e.HasIndex(x => x.ExpiresAt, "ix_buddyrequests_pending_expires")
             .HasFilter($"\"Status\" = '{BuddyRequestStatuses.Pending}'");
            e.HasIndex(x => x.EndedAt, "ix_buddyrequests_ended")
             .HasFilter("\"EndedAt\" IS NOT NULL");
        });

        modelBuilder.Entity<BuddyLink>(e =>
        {
            e.ToTable("Buddies", t => t.HasCheckConstraint("ck_buddies_not_self", "\"UserId\" <> \"BuddyUserId\""));
            e.HasKey(x => new { x.UserId, x.BuddyUserId });
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");

            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.BuddyUserId).OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => x.BuddyUserId, "ix_buddies_buddy");
            e.HasIndex(x => x.UserId, "ix_buddies_user_unseen").HasFilter("\"SeenAt\" IS NULL");
        });

        modelBuilder.Entity<TripMember>(e =>
        {
            e.ToTable("TripMembers", t => t.HasCheckConstraint("ck_tripmembers_status", $"\"Status\" IN ({TripMemberStatuses.SqlList})"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.Status).HasMaxLength(10).IsRequired();
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
            e.Property(x => x.Version).HasDefaultValue(1).HasSentinel(1);

            e.HasOne<Trip>().WithMany().HasForeignKey(x => x.TripId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => new { x.TripId, x.UserId }, "ux_tripmembers_trip_user").IsUnique();
            e.HasIndex(x => new { x.TripId, x.Status }, "ix_tripmembers_trip_status");
            e.HasIndex(x => new { x.UserId, x.JoinedAt, x.Id }, "ix_tripmembers_user_joined")
             .IsDescending(false, true, true)
             .HasFilter($"\"Status\" = '{TripMemberStatuses.Joined}'");
            e.HasIndex(x => new { x.UserId, x.Status }, "ix_tripmembers_user_status");
            e.HasIndex(x => x.InviteExpiresAt, "ix_tripmembers_invite_expires")
             .HasFilter($"\"Status\" = '{TripMemberStatuses.Invited}'");
            e.HasIndex(x => x.EndedAt, "ix_tripmembers_ended")
             .HasFilter($"\"Status\" IN ({TripMemberStatuses.InviteEndedSqlList})");
        });

        modelBuilder.Entity<TripExpense>(e =>
        {
            e.ToTable("TripExpenses", t =>
            {
                t.HasCheckConstraint("ck_tripexpenses_amount", $"\"Amount\" BETWEEN {BuddyLimits.MinAmount} AND {BuddyLimits.MaxAmount}");
                t.HasCheckConstraint("ck_tripexpenses_category", $"\"Category\" IN ({ExpenseCategories.SqlList})");
                t.HasCheckConstraint("ck_tripexpenses_splitmode", $"\"SplitMode\" IN ({SplitModes.SqlList})");
                t.HasCheckConstraint("ck_tripexpenses_day", "\"DayNumber\" >= 1");
                t.HasCheckConstraint("ck_tripexpenses_link", "(\"LinkKind\" IS NULL) = (\"LinkId\" IS NULL)");
                t.HasCheckConstraint("ck_tripexpenses_linkkind", $"\"LinkKind\" IS NULL OR \"LinkKind\" IN ({ExpenseLinkKinds.SqlList})");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Title).HasMaxLength(BuddyLimits.TitleMax).IsRequired();
            e.Property(x => x.Category).HasMaxLength(10).IsRequired();
            e.Property(x => x.LinkKind).HasMaxLength(5);
            e.Property(x => x.SplitMode).HasMaxLength(6).IsRequired();
            e.Property(x => x.Note).HasMaxLength(BuddyLimits.NoteMax);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");

            e.HasOne<Trip>().WithMany().HasForeignKey(x => x.TripId).OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => new { x.TripId, x.DayNumber, x.CreatedAt, x.Id }, "ix_tripexpenses_trip_day_live")
             .IsDescending(false, true, true, true)
             .IncludeProperties(x => new { x.Amount, x.PaidByUserId })
             .HasFilter("\"DeletedAt\" IS NULL");
            e.HasIndex(x => new { x.TripId, x.LinkKind, x.LinkId }, "ix_tripexpenses_trip_link")
             .IncludeProperties(x => new { x.Amount })
             .HasFilter("\"LinkId\" IS NOT NULL AND \"DeletedAt\" IS NULL");
            e.HasIndex(x => x.TripId, "ix_tripexpenses_trip");
        });

        modelBuilder.Entity<TripExpenseShare>(e =>
        {
            e.ToTable("TripExpenseShares", t => t.HasCheckConstraint("ck_tripexpenseshares_share", "\"Share\" > 0"));
            e.HasKey(x => new { x.ExpenseId, x.UserId });

            e.HasOne<TripExpense>().WithMany().HasForeignKey(x => x.ExpenseId).OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => new { x.TripId, x.UserId }, "ix_tripexpenseshares_trip_user")
             .IncludeProperties(x => new { x.Share });
        });

        modelBuilder.Entity<TripExpenseRevision>(e =>
        {
            e.ToTable("TripExpenseRevisions", t => t.HasCheckConstraint("ck_tripexpenserevisions_kind", $"\"Kind\" IN ({RevisionKinds.SqlList})"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.Kind).HasMaxLength(10).IsRequired();
            e.Property(x => x.ChangesJson).HasColumnType("jsonb").IsRequired();
            e.Property(x => x.SharesAfterJson).HasColumnType("jsonb").IsRequired();
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");

            e.HasOne<TripExpense>().WithMany().HasForeignKey(x => x.ExpenseId).OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => new { x.ExpenseId, x.Version }, "ux_tripexpenserevisions_expense_version").IsUnique();
            e.HasIndex(x => new { x.ExpenseId, x.OpId }, "ux_tripexpenserevisions_expense_op").IsUnique();
        });

        modelBuilder.Entity<TripPayment>(e =>
        {
            e.ToTable("TripPayments", t =>
            {
                t.HasCheckConstraint("ck_trippayments_state", $"\"State\" IN ({PaymentStates.SqlList})");
                t.HasCheckConstraint("ck_trippayments_not_self", "\"FromUserId\" <> \"ToUserId\"");
                t.HasCheckConstraint("ck_trippayments_amount", $"\"Amount\" BETWEEN {BuddyLimits.MinPaymentAmount} AND {BuddyLimits.MaxPaymentAmount}");
                t.HasCheckConstraint("ck_trippayments_received", $"\"State\" = '{PaymentStates.Received}' OR \"ReceivedAt\" IS NULL");
                t.HasCheckConstraint("ck_trippayments_ended", $"(\"State\" IN ({PaymentStates.EndedSqlList})) = (\"EndedAt\" IS NOT NULL)");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.State).HasMaxLength(11).IsRequired();
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");

            e.HasOne<Trip>().WithMany().HasForeignKey(x => x.TripId).OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => new { x.TripId, x.State }, "ix_trippayments_trip_state")
             .IncludeProperties(x => new { x.FromUserId, x.ToUserId, x.Amount });
            e.HasIndex(x => new { x.TripId, x.FromUserId, x.ToUserId }, "ux_trippayments_pair_saidpaid")
             .IsUnique()
             .HasFilter($"\"State\" = '{PaymentStates.SaidPaid}'");
            e.HasIndex(x => x.EndedAt, "ix_trippayments_ended").HasFilter("\"EndedAt\" IS NOT NULL");
        });

        modelBuilder.Entity<TripExpenseSuggestionSkip>(e =>
        {
            e.ToTable("TripExpenseSuggestionSkips");
            e.HasKey(x => new { x.TripId, x.SourceKind, x.SourceId });
            e.Property(x => x.SourceKind).HasMaxLength(5);
            e.Property(x => x.SkippedAt).HasDefaultValueSql("now()");

            e.HasOne<Trip>().WithMany().HasForeignKey(x => x.TripId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TripExpenseState>(e =>
        {
            e.ToTable("TripExpenseState");
            e.HasKey(x => x.TripId);
            e.Property(x => x.TripId).ValueGeneratedNever();
            e.Property(x => x.Version).HasDefaultValue(0L);
            e.Property(x => x.ExpenseCount).HasDefaultValue(0);
            e.Property(x => x.TotalSpent).HasDefaultValue(0L);
            e.Property(x => x.OpenPayments).HasDefaultValue(0);
            e.Property(x => x.SaidPaidCount).HasDefaultValue(0);
            e.Property(x => x.ReceivedCount).HasDefaultValue(0);
            e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");

            e.HasOne<Trip>().WithOne().HasForeignKey<TripExpenseState>(x => x.TripId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserNotice>(e =>
        {
            e.ToTable("UserNotices", t =>
            {
                t.HasCheckConstraint("ck_usernotices_kind", $"\"Kind\" IN ({NoticeKinds.SqlList})");
                t.HasCheckConstraint("ck_usernotices_count", "\"Count\" >= 1");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.Kind).HasMaxLength(20).IsRequired();
            e.Property(x => x.TripId).HasDefaultValue(NoticeKinds.NoTrip);
            e.Property(x => x.Count).HasDefaultValue(1).HasSentinel(1);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");

            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => new { x.UserId, x.Kind, x.TripId }, "ux_usernotices_user_kind_trip").IsUnique();
            e.HasIndex(x => new { x.UserId, x.UpdatedAt, x.Id }, "ix_usernotices_user_updated")
             .IsDescending(false, true, true);
            e.HasIndex(x => x.UserId, "ix_usernotices_user_unread").HasFilter("\"ReadAt\" IS NULL");
            e.HasIndex(x => new { x.TripId, x.Kind }, "ix_usernotices_trip_kind");
            e.HasIndex(x => x.UpdatedAt, "ix_usernotices_updated");
        });

        // E1 links and S14 read the bookings of one trip; no index started with TripId before.
        modelBuilder.Entity<TaxiBooking>().HasIndex(x => x.TripId, "ix_taxibookings_trip");
    }
}
