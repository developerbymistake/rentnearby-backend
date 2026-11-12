using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.Retention;
using RentNearBy.Infrastructure.Services.Retention.GroupA;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;

namespace RentNearBy.Tests.Retention.GroupA;

public class ChatRetentionRuleSqlTests
{
    [Fact]
    public void Rule_identity_and_defaults()
    {
        var r = new ChatRetentionRule();
        Assert.Equal("Chat", r.Name);
        Assert.Equal(RetentionGroups.Chat, r.Group);
        Assert.Equal(90, r.DefaultDays);
        Assert.True(r.DefaultEnabled);
        Assert.False(r.GuardExempt);
    }

    [Fact]
    public void Eligibility_uses_last_activity_and_no_newer_message()
    {
        var sql = ChatRetentionRule.EligibleSql;
        Assert.Contains("GREATEST(c.\"LastMessageAt\", c.\"CreatedAt\") < @cutoff", sql);
        Assert.Contains("m.\"CreatedAt\" >= @cutoff", sql);
    }

    [Fact]
    public void Protection_covers_enquiry_states_and_reports_three_ways()
    {
        var p = ChatRetentionRule.ProtectionSql;
        Assert.Contains(TripEnquiryStatuses.OpenSqlList, p);
        Assert.Contains("e.\"UpdatedAt\" >= @cutoff", p);
        Assert.Contains("e.\"Status\" = 'Failed'", p);
        Assert.Contains("ConfirmedByTravellerAt", p);
        Assert.Contains("ConfirmedByOwnerAt", p);
        Assert.Contains("BookedByTravellerAt", p);
        Assert.Contains("AdvanceReceivedAt", p);
        Assert.Contains("AdvanceStatus", p);
        Assert.Contains("AcceptedOfferId", p);
        Assert.Contains("tr.\"EndDate\" IS NULL OR tr.\"EndDate\" >= @today", p);
        Assert.Contains("\"ListingReports\"", p);
        Assert.Contains("r.\"TripEnquiryId\" IN", p);
        Assert.Contains("r.\"ReporterUserId\" = c.\"RenterId\" AND r.\"ReportedUserId\" = c.\"OwnerId\"", p);
        Assert.Contains("r.\"ReporterUserId\" = c.\"OwnerId\" AND r.\"ReportedUserId\" = c.\"RenterId\"", p);
        Assert.Contains("r.\"ListingId\" = c.\"ListingId\" AND r.\"ListingType\" = c.\"ListingType\"", p);
        Assert.Contains("r.\"Status\" <> 'Resolved'", p);
    }

    [Fact]
    public void Selection_is_locked_skip_locked_and_limited()
    {
        Assert.EndsWith("LIMIT @n FOR UPDATE SKIP LOCKED", ChatRetentionRule.SelectIdsSql);
        Assert.Contains("ORDER BY c.\"LastMessageAt\"", ChatRetentionRule.SelectIdsSql);
        Assert.Equal(200, ChatRetentionRule.ConversationBatch);
    }

    [Fact]
    public void Answers_are_deleted_before_the_rest_and_both_recheck_protection()
    {
        Assert.Contains("\"RespondsToMessageId\" IS NOT NULL", ChatRetentionRule.DeleteAnswersSql);
        Assert.DoesNotContain("m.\"RespondsToMessageId\" IS NOT NULL", ChatRetentionRule.DeleteRestSql);
        foreach (var sql in new[] { ChatRetentionRule.DeleteAnswersSql, ChatRetentionRule.DeleteRestSql })
        {
            Assert.Contains("m.\"CreatedAt\" < @cutoff", sql);
            Assert.Contains("ANY(@ids)", sql);
            Assert.Contains("\"ListingReports\"", sql);
        }
        Assert.Contains("a.\"RespondsToMessageId\" = m.\"Id\"", ChatRetentionRule.DeleteRestSql);
    }

    [Fact]
    public void Conversation_delete_only_when_no_messages_left_and_unprotected()
    {
        var sql = ChatRetentionRule.DeleteConversationsSql;
        Assert.Contains("NOT EXISTS (SELECT 1 FROM \"Messages\" m WHERE m.\"ConversationId\" = c.\"Id\")", sql);
        Assert.Contains("\"ListingReports\"", sql);
    }

    [Fact]
    public void Messages_are_never_deleted_without_their_conversation_check()
    {
        Assert.DoesNotContain("DELETE FROM \"Messages\" WHERE", ChatRetentionRule.DeleteRestSql);
        Assert.Contains("\"Conversations\" c WHERE c.\"Id\" = ANY(@ids)", ChatRetentionRule.DeleteRestSql);
    }
}

public class ChatRetentionRuleTests(TestDatabase db) : IntegrationTestBase(db)
{
    static readonly DateTime Now = DateTime.UtcNow;
    static readonly DateTime Old = Now.AddDays(-200);

    static RetentionContext Ctx(ApplicationDbContext c) =>
        new(c, Now, Now.AddDays(-90), DateOnly.FromDateTime(Now), DateOnly.FromDateTime(Now.AddDays(-90)), 90);

    static async Task RunAsync(ApplicationDbContext c)
    {
        var rule = new ChatRetentionRule();
        while (await rule.DeleteBatchAsync(Ctx(c), 200, CancellationToken.None) > 0) { }
    }

    static Conversation Conv(DateTime last, int unread = 0) => new()
    {
        Id = Guid.NewGuid(), RenterId = Guid.NewGuid(), OwnerId = Guid.NewGuid(), ListingType = "Stay", ListingId = Guid.NewGuid(),
        CreatedAt = last, LastMessageAt = last, UnreadCountForOwner = unread,
    };

    static Message Msg(Conversation c, DateTime at, Guid? respondsTo = null) => new()
    {
        Id = Guid.NewGuid(), ConversationId = c.Id, SenderId = c.RenterId, Type = "quick_reply", CreatedAt = at, RespondsToMessageId = respondsTo,
    };

    static async Task<Guid> ReasonAsync(ApplicationDbContext c)
    {
        var r = new ReportReason { Id = Guid.NewGuid(), Name = "t" + Guid.NewGuid(), CreatedAt = Now };
        c.ReportReasons.Add(r);
        await c.SaveChangesAsync();
        return r.Id;
    }

    static ListingReport Report(Conversation c, string status, Guid reason, Guid? enquiryId = null, bool swapped = false) => new()
    {
        Id = Guid.NewGuid(), ListingId = Guid.NewGuid(), ListingType = "Stay", ReasonId = reason, Status = status, CreatedAt = Old,
        ResolvedAt = status == "Resolved" ? Old : null, TripEnquiryId = enquiryId,
        ReporterUserId = swapped ? c.OwnerId : enquiryId == null ? c.RenterId : Guid.NewGuid(),
        ReportedUserId = swapped ? c.RenterId : enquiryId == null ? c.OwnerId : Guid.NewGuid(),
    };

    [IntegrationFact]
    public async Task Old_chat_goes_answers_first_and_empty_conversation_goes_too()
    {
        await using var c = CreateContext();
        var conv = Conv(Old);
        var q = Msg(conv, Old);
        var a = Msg(conv, Old, q.Id);
        var empty = Conv(Old);
        c.Conversations.AddRange(conv, empty);
        await c.SaveChangesAsync();
        c.Messages.AddRange(q, a);
        await c.SaveChangesAsync();

        await RunAsync(c);

        Assert.False(await c.Conversations.AnyAsync(x => x.Id == conv.Id || x.Id == empty.Id));
        Assert.False(await c.Messages.AnyAsync(x => x.ConversationId == conv.Id));
    }

    [IntegrationFact]
    public async Task Newer_message_keeps_whole_chat_and_unread_count()
    {
        await using var c = CreateContext();
        var conv = Conv(Old, unread: 3);
        c.Conversations.Add(conv);
        await c.SaveChangesAsync();
        c.Messages.AddRange(Msg(conv, Old), Msg(conv, Now.AddDays(-5)));
        await c.SaveChangesAsync();

        await RunAsync(c);

        Assert.Equal(2, await c.Messages.CountAsync(x => x.ConversationId == conv.Id));
        Assert.Equal(3, (await c.Conversations.AsNoTracking().SingleAsync(x => x.Id == conv.Id)).UnreadCountForOwner);
    }

    [IntegrationFact]
    public async Task Pending_report_by_party_pair_either_direction_keeps_chat()
    {
        await using var c = CreateContext();
        var reason = await ReasonAsync(c);
        var a = Conv(Old);
        var b = Conv(Old);
        c.Conversations.AddRange(a, b);
        await c.SaveChangesAsync();
        c.Messages.AddRange(Msg(a, Old), Msg(b, Old));
        c.ListingReports.AddRange(Report(a, "Pending", reason), Report(b, "Pending", reason, swapped: true));
        await c.SaveChangesAsync();

        await RunAsync(c);

        Assert.True(await c.Messages.AnyAsync(x => x.ConversationId == a.Id));
        Assert.True(await c.Messages.AnyAsync(x => x.ConversationId == b.Id));
    }

    [IntegrationFact]
    public async Task Resolved_old_report_does_not_keep_chat()
    {
        await using var c = CreateContext();
        var reason = await ReasonAsync(c);
        var conv = Conv(Old);
        c.Conversations.Add(conv);
        await c.SaveChangesAsync();
        c.Messages.Add(Msg(conv, Old));
        c.ListingReports.Add(Report(conv, "Resolved", reason));
        await c.SaveChangesAsync();

        await RunAsync(c);

        Assert.False(await c.Conversations.AnyAsync(x => x.Id == conv.Id));
    }

    [IntegrationFact]
    public async Task Pending_report_by_enquiry_link_and_open_enquiry_keep_chat()
    {
        await using var c = CreateContext();
        var reason = await ReasonAsync(c);
        var linked = Conv(Old);
        var open = Conv(Old);
        c.Conversations.AddRange(linked, open);
        await c.SaveChangesAsync();
        c.Messages.AddRange(Msg(linked, Old), Msg(open, Old));
        var closedEnq = new TripEnquiry { Id = Guid.NewGuid(), ConversationId = linked.Id, Status = TripEnquiryStatuses.Closed, ListingId = Guid.NewGuid(), CreatedAt = Old, UpdatedAt = Old };
        var openEnq = new TripEnquiry { Id = Guid.NewGuid(), ConversationId = open.Id, Status = TripEnquiryStatuses.Asked, ListingId = Guid.NewGuid(), CreatedAt = Old, UpdatedAt = Old };
        c.TripEnquiries.AddRange(closedEnq, openEnq);
        c.ListingReports.Add(Report(linked, "Pending", reason, enquiryId: closedEnq.Id));
        await c.SaveChangesAsync();

        await RunAsync(c);

        Assert.True(await c.Messages.AnyAsync(x => x.ConversationId == linked.Id));
        Assert.True(await c.Messages.AnyAsync(x => x.ConversationId == open.Id));
    }

    [IntegrationFact]
    public async Task Booked_closed_enquiry_keeps_chat()
    {
        await using var c = CreateContext();
        var conv = Conv(Old);
        c.Conversations.Add(conv);
        await c.SaveChangesAsync();
        c.Messages.Add(Msg(conv, Old));
        c.TripEnquiries.Add(new TripEnquiry
        {
            Id = Guid.NewGuid(), ConversationId = conv.Id, Status = TripEnquiryStatuses.Closed, ListingId = Guid.NewGuid(),
            BookedByTravellerAt = Old, CreatedAt = Old, UpdatedAt = Old,
        });
        await c.SaveChangesAsync();

        await RunAsync(c);

        Assert.True(await c.Messages.AnyAsync(x => x.ConversationId == conv.Id));
    }

    [IntegrationFact]
    public async Task Push_log_of_open_enquiry_and_events_with_reads_follow_group_a_rules()
    {
        await using var c = CreateContext();
        var openEnq = new TripEnquiry { Id = Guid.NewGuid(), Status = TripEnquiryStatuses.Asked, ListingId = Guid.NewGuid(), CreatedAt = Old, UpdatedAt = Old };
        c.TripEnquiries.Add(openEnq);
        var kept = new TripPushLog { Id = Guid.NewGuid(), TripEnquiryId = openEnq.Id, DedupeKey = "k" + Guid.NewGuid(), Kind = "x", SentAt = Old };
        var gone = new TripPushLog { Id = Guid.NewGuid(), DedupeKey = "k" + Guid.NewGuid(), Kind = "x", SentAt = Old };
        c.TripPushLogs.AddRange(kept, gone);
        await c.SaveChangesAsync();

        foreach (var def in new GroupARetentionRules().Create().Where(r => r.Name == "TripPushLogs"))
            while (await def.DeleteBatchAsync(Ctx(c), 1000, CancellationToken.None) > 0) { }

        Assert.True(await c.TripPushLogs.AnyAsync(x => x.Id == kept.Id));
        Assert.False(await c.TripPushLogs.AnyAsync(x => x.Id == gone.Id));
    }
}
