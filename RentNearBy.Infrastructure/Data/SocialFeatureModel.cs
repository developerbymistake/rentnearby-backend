using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Entities.Social;
using RentNearBy.Core.Models.Social;

namespace RentNearBy.Infrastructure.Data;

// Bakhli social (CONTRACT_SOCIAL section 1). No DbSet properties: code uses Set<T>() and raw SQL.
public static class SocialFeatureModel
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserProfile>(e =>
        {
            e.ToTable("UserProfiles", t =>
            {
                t.HasCheckConstraint("ck_userprofiles_gender", $"\"Gender\" IN ({SocialGenders.SqlList})");
                t.HasCheckConstraint("ck_userprofiles_birthmonth", "\"BirthMonth\" BETWEEN 1 AND 12");
                t.HasCheckConstraint("ck_userprofiles_birthyear", $"\"BirthYear\" BETWEEN {SocialLimits.BirthYearMin} AND {SocialLimits.BirthYearMax}");
                t.HasCheckConstraint("ck_userprofiles_messagepolicy", $"\"MessagePolicy\" IN ({MessagePolicies.SqlList})");
                t.HasCheckConstraint("ck_userprofiles_followers", "\"FollowerCount\" >= 0");
                t.HasCheckConstraint("ck_userprofiles_following", "\"FollowingCount\" >= 0");
            });
            e.HasKey(x => x.UserId);
            e.Property(x => x.Gender).HasMaxLength(6).IsRequired();
            e.Property(x => x.BirthMonth).HasColumnType("smallint");
            e.Property(x => x.BirthYear).HasColumnType("smallint");
            e.Property(x => x.About).HasMaxLength(SocialLimits.AboutMax).IsRequired();
            e.Property(x => x.Interests).HasColumnType("text[]").IsRequired();
            e.Property(x => x.Skills).HasColumnType("text[]").IsRequired();
            e.Property(x => x.Places).HasColumnType("text[]").IsRequired();
            e.Property(x => x.HomeCityName).HasMaxLength(SocialLimits.HomeCityMax);
            e.Property(x => x.HomeStateName).HasMaxLength(SocialLimits.HomeStateMax);
            e.Property(x => x.IsPrivate).HasDefaultValue(false);
            e.Property(x => x.MessagePolicy).HasMaxLength(10).HasDefaultValue(MessagePolicies.Everyone).IsRequired();
            e.Property(x => x.NotifyMessages).HasDefaultValue(true).HasSentinel(true);
            e.Property(x => x.NotifyFollows).HasDefaultValue(true).HasSentinel(true);
            e.Property(x => x.NotifyFollowedTrips).HasDefaultValue(true).HasSentinel(true);
            e.Property(x => x.FollowerCount).HasDefaultValue(0);
            e.Property(x => x.FollowingCount).HasDefaultValue(0);
            e.Property(x => x.CompletedAt).HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
            e.HasOne<User>().WithOne().HasForeignKey<UserProfile>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProfileIdentityAudit>(e =>
        {
            e.ToTable("ProfileIdentityAudit");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.OldGender).HasMaxLength(6).IsRequired();
            e.Property(x => x.NewGender).HasMaxLength(6).IsRequired();
            e.Property(x => x.OldBirthMonth).HasColumnType("smallint");
            e.Property(x => x.NewBirthMonth).HasColumnType("smallint");
            e.Property(x => x.OldBirthYear).HasColumnType("smallint");
            e.Property(x => x.NewBirthYear).HasColumnType("smallint");
            e.Property(x => x.OldName).HasMaxLength(SocialLimits.NameMax);
            e.Property(x => x.NewName).HasMaxLength(SocialLimits.NameMax);
            e.Property(x => x.Reason).HasMaxLength(SocialLimits.IdentityReasonMax).IsRequired();
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.UserId, x.CreatedAt }, "ix_profileidentityaudit_user").IsDescending(false, true);
        });

        modelBuilder.Entity<Follow>(e =>
        {
            e.ToTable("Follows", t =>
            {
                t.HasCheckConstraint("ck_follows_status", $"\"Status\" IN ({FollowStatuses.SqlList})");
                t.HasCheckConstraint("ck_follows_not_self", "\"FollowerId\" <> \"FolloweeId\"");
            });
            e.HasKey(x => new { x.FollowerId, x.FolloweeId });
            e.Property(x => x.Status).HasColumnType("char(1)").IsRequired();
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.HasOne<User>().WithMany().HasForeignKey(x => x.FollowerId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.FolloweeId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.FollowerId, x.CreatedAt, x.FolloweeId }, "ix_follows_following")
             .IsDescending(false, true, false)
             .HasFilter($"\"Status\" = '{FollowStatuses.Accepted}'");
            e.HasIndex(x => new { x.FolloweeId, x.CreatedAt, x.FollowerId }, "ix_follows_followers")
             .IsDescending(false, true, false)
             .HasFilter($"\"Status\" = '{FollowStatuses.Accepted}'");
            e.HasIndex(x => new { x.FolloweeId, x.CreatedAt, x.FollowerId }, "ix_follows_requests")
             .IsDescending(false, true, false)
             .HasFilter($"\"Status\" = '{FollowStatuses.Requested}'");
        });

        modelBuilder.Entity<Trip>(e =>
        {
            e.ToTable(t =>
            {
                t.HasCheckConstraint("ck_trips_joingender", $"\"JoinGender\" IN ({JoinGenders.SqlList})");
                t.HasCheckConstraint("ck_trips_seats", "\"Seats\" IS NULL OR \"Seats\" BETWEEN 1 AND 11");
                t.HasCheckConstraint("ck_trips_postdays", "\"PostDays\" IS NULL OR \"PostDays\" BETWEEN 1 AND 11");
                t.HasCheckConstraint("ck_trips_postwindow", "\"PostWindowMonths\" IS NULL OR \"PostWindowMonths\" BETWEEN 1 AND 6");
                t.HasCheckConstraint("ck_trips_public_fields",
                    "NOT \"IsPublic\" OR (\"PublishedAt\" IS NOT NULL AND \"Seats\" IS NOT NULL AND \"PostDays\" IS NOT NULL)");
            });
            e.Property(x => x.IsPublic).HasDefaultValue(false);
            e.Property(x => x.PostText).HasMaxLength(SocialLimits.PostTextMax);
            e.Property(x => x.PostDays).HasColumnType("smallint");
            e.Property(x => x.PostWindowMonths).HasColumnType("smallint");
            e.Property(x => x.JoinGender).HasColumnType("char(1)").HasDefaultValue(JoinGenders.Anyone).IsRequired();
            e.Property(x => x.Seats).HasColumnType("smallint");
            e.Property(x => x.PostCityNames).HasColumnType("text[]").HasDefaultValueSql("'{}'::text[]").IsRequired();
            e.Property(x => x.PostStateName).HasMaxLength(SocialLimits.HomeStateMax);
            e.Property(x => x.InactiveByPlan).HasDefaultValue(false);
            e.Property(x => x.PausedByPlan).HasDefaultValue(false);
            e.HasIndex(x => new { x.PublishedAt, x.Id }, "ix_trips_public").IsDescending(true, true).HasFilter("\"IsPublic\"");
        });

        modelBuilder.Entity<TripJoinRequest>(e =>
        {
            e.ToTable("TripJoinRequests", t =>
            {
                t.HasCheckConstraint("ck_tripjoinrequests_status", $"\"Status\" IN ({JoinRequestStatuses.SqlList})");
                t.HasCheckConstraint("ck_tripjoinrequests_not_self", "\"OwnerUserId\" <> \"RequesterId\"");
                t.HasCheckConstraint("ck_tripjoinrequests_tier", "\"Tier\" BETWEEN 0 AND 2");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.Note).HasMaxLength(SocialLimits.JoinNoteMax);
            e.Property(x => x.Tier).HasColumnType("smallint");
            e.Property(x => x.Status).HasMaxLength(14).IsRequired();
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.HasOne<Trip>().WithMany().HasForeignKey(x => x.TripId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.RequesterId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.RequesterId, x.ClientRequestId }, "ux_tripjoinrequests_requester_client").IsUnique();
            e.HasIndex(x => new { x.TripId, x.RequesterId }, "ux_tripjoinrequests_live")
             .IsUnique()
             .HasFilter($"\"Status\" = '{JoinRequestStatuses.Waiting}'");
            e.HasIndex(x => new { x.TripId, x.Tier, x.CreatedAt }, "ix_tripjoinrequests_trip")
             .IsDescending(false, false, true)
             .HasFilter($"\"Status\" = '{JoinRequestStatuses.Waiting}'");
            e.HasIndex(x => new { x.RequesterId, x.CreatedAt }, "ix_tripjoinrequests_requester").IsDescending(false, true);
            e.HasIndex(x => x.EndedAt, "ix_tripjoinrequests_ended").HasFilter("\"EndedAt\" IS NOT NULL");
        });

        modelBuilder.Entity<ChatRoom>(e =>
        {
            e.ToTable("ChatRooms", t =>
            {
                t.HasCheckConstraint("ck_chatrooms_kind", $"\"Kind\" IN ({ChatKinds.SqlList})");
                t.HasCheckConstraint("ck_chatrooms_status", $"\"Status\" IN ({ChatRoomStatuses.SqlList})");
                t.HasCheckConstraint("ck_chatrooms_pair",
                    $"(\"Kind\" = '{ChatKinds.Direct}' AND \"PairLowId\" IS NOT NULL AND \"PairHighId\" IS NOT NULL AND \"PairLowId\" < \"PairHighId\" AND \"TripId\" IS NULL)"
                    + $" OR (\"Kind\" = '{ChatKinds.Trip}' AND \"TripId\" IS NOT NULL AND \"PairLowId\" IS NULL AND \"PairHighId\" IS NULL)");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.Kind).HasColumnType("char(1)").IsRequired();
            e.Property(x => x.Status).HasMaxLength(10).IsRequired();
            e.Property(x => x.LastSeq).HasDefaultValue(0L);
            e.Property(x => x.LastPreview).HasMaxLength(SocialLimits.ChatPreviewMax);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.HasOne<Trip>().WithMany().HasForeignKey(x => x.TripId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.PairLowId, x.PairHighId }, "ux_chatrooms_pair")
             .IsUnique()
             .HasFilter($"\"Kind\" = '{ChatKinds.Direct}'");
            e.HasIndex(x => x.TripId, "ux_chatrooms_trip")
             .IsUnique()
             .HasFilter($"\"Kind\" = '{ChatKinds.Trip}'");
            e.HasIndex(x => new { x.RequestedByUserId, x.ChargedAt }, "ix_chatrooms_charged")
             .IsDescending(false, true)
             .HasFilter("\"ChargedAt\" IS NOT NULL");
            e.HasIndex(x => x.LastMessageAt, "ix_chatrooms_lastmessage").HasFilter($"\"Kind\" = '{ChatKinds.Direct}'");
            // The 10-minute preview purge (ChatExpiryPurgeJob.ClearPreviewSql) reads only rooms that still show a preview.
            e.HasIndex(x => x.LastMessageAt, "ix_chatrooms_preview").HasFilter("\"LastPreview\" IS NOT NULL");
        });

        modelBuilder.Entity<ChatRoomMember>(e =>
        {
            e.ToTable("ChatRoomMembers", t =>
            {
                t.HasCheckConstraint("ck_chatmembers_bucket", $"\"ListBucket\" IN ({ChatBuckets.SqlList})");
                t.HasCheckConstraint("ck_chatmembers_unread", "\"UnreadCount\" >= 0");
            });
            e.HasKey(x => new { x.RoomId, x.UserId });
            e.Property(x => x.LastReadSeq).HasDefaultValue(0L);
            e.Property(x => x.UnreadCount).HasDefaultValue(0);
            e.Property(x => x.ListBucket).HasColumnType("char(1)").IsRequired();
            e.Property(x => x.ClearedUpToSeq).HasDefaultValue(0L);
            e.Property(x => x.JoinedSeq).HasDefaultValue(0L);
            e.Property(x => x.SortAt).HasDefaultValueSql("now()");
            e.HasOne<ChatRoom>().WithMany().HasForeignKey(x => x.RoomId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.UserId, x.ListBucket, x.SortAt, x.RoomId }, "ix_chatmembers_list")
             .IsDescending(false, false, true, false);
            e.HasIndex(x => x.UserId, "ix_chatmembers_unread")
             .IncludeProperties(x => new { x.UnreadCount, x.ListBucket })
             .HasFilter("\"UnreadCount\" > 0");
        });

        modelBuilder.Entity<ChatMessage>(e =>
        {
            e.ToTable("ChatMessages", t =>
                t.HasCheckConstraint("ck_chatmessages_system", $"\"SystemKind\" IS NULL OR \"SystemKind\" IN ({ChatSystemKinds.SqlList})"));
            e.HasKey(x => new { x.RoomId, x.Seq });
            e.Property(x => x.Seq).ValueGeneratedNever();
            e.Property(x => x.SystemKind).HasMaxLength(8);
            e.Property(x => x.Body).HasMaxLength(SocialLimits.ChatTextMax).IsRequired();
            e.Property(x => x.Masked).HasDefaultValue(false);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.HasOne<ChatRoom>().WithMany().HasForeignKey(x => x.RoomId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.RoomId, x.SenderId, x.ClientMessageId }, "ux_chatmessages_client")
             .IsUnique()
             .HasFilter("\"ClientMessageId\" IS NOT NULL");
            e.HasIndex(x => x.ExpiresAt, "ix_chatmessages_expiry");
        });

        modelBuilder.Entity<MessageSnapshot>(e =>
        {
            e.ToTable("MessageSnapshots");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.MessagesJson).HasColumnType("jsonb").IsRequired();
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.HasOne<ListingReport>().WithMany().HasForeignKey(x => x.ReportId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => x.PurgeAt, "ix_messagesnapshots_purge");
            e.HasIndex(x => x.ReportId, "ix_messagesnapshots_report").HasFilter("\"ReportId\" IS NOT NULL");
        });

        modelBuilder.Entity<ListingReport>(e => e.Property(x => x.MessageSeq));
    }
}
