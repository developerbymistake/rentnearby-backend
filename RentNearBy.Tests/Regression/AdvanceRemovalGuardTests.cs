using System.Reflection;
using System.Text.RegularExpressions;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RentNearBy.Api.Endpoints;
using RentNearBy.Api.Extensions;
using RentNearBy.Api.Validators;
using RentNearBy.Tests.TaxiRequests;

namespace RentNearBy.Tests.Regression;

// Owner decision 2026-11-21: the app never asks for an advance, never asks for or shows proof, never holds a booking for
// money and never tracks payment between two users. Contract F:/Dhasu/Buddy_resume/remove_advance/CONTRACT_REMOVE_ADVANCE.md.
public class AdvanceRemovalGuardTests
{
    private static List<(string Template, string Method)> Routes()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DATABASE_URL"] = "Host=localhost;Database=test;Username=test;Password=test",
            ["Jwt:Key"] = "unit-test-signing-key-unit-test-signing-key",
        });
        builder.Services.AddApplicationServices(builder.Configuration);
        builder.Services.AddValidatorsFromAssemblyContaining<SendOtpRequestValidator>();
        var app = builder.Build();
        var built = new List<(string, string)>();
        void Collect(RouteGroupBuilder g, string prefix) => ((IEndpointConventionBuilder)g).Finally(b =>
        {
            var route = (RouteEndpointBuilder)b;
            var methods = route.Metadata.OfType<IHttpMethodMetadata>().LastOrDefault()?.HttpMethods ?? [];
            foreach (var m in methods) built.Add((prefix + "/" + route.RoutePattern.RawText!.Trim('/'), m));
        });
        var trips = app.MapGroup("/api/v1/trips");
        trips.MapTripEndpoints();
        Collect(trips, "");
        var stays = app.MapGroup("/api/v1/stays");
        stays.MapStayEndpoints();
        Collect(stays, "");
        var chat = app.MapGroup("/api/v1/chat");
        chat.MapChatEndpoints();
        Collect(chat, "");
        var admin = app.MapGroup("/api/v1/admin");
        admin.MapAdminEndpoints();
        Collect(admin, "");
        _ = ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints).ToList();
        return built.Select(x => (x.Item1.Replace("//", "/"), x.Item2)).ToList();
    }

    [Fact]
    public void No_advance_route_is_mapped_and_the_share_route_is()
    {
        var routes = Routes();
        Assert.NotEmpty(routes);
        Assert.DoesNotContain(routes, r => r.Template.Contains("/advance", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(routes, r => r.Template.Contains("proof", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(routes, r => r.Template.EndsWith("/stays/me/share-details") && r.Method == "PUT");
        Assert.Contains(routes, r => r.Template.EndsWith("/chat/conversations/{conversationId:guid}/messages") && r.Method == "POST");
    }

    private static readonly Regex Banned = new(@"(?i)advance|proofsent|proof-sent|holduntil|holdexpired", RegexOptions.Compiled);

    // Exact lines that may keep the word: guest safety report reason and chat question templates (judgment call J2), the
    // proxy comment in Program.cs. A later private own-record feature adds its own folder here.
    private static readonly string[] Allowed =
    [
        "Name = \"Asked for advance payment\"",
        "Key = \"stay_advance_needed\"",
        "QuestionText = \"Is an advance needed?\"",
        "\\\"key\\\":\\\"advance_needed\\\"",
        "\\\"key\\\":\\\"pay_arrival_advance\\\"",
        "known-in-advance",
    ];

    // Owner roles money notes (doc 6.2): the owner's own record of an advance he received, never a payment. Only these forms,
    // only in owner files (folders named Owner, files named Owner*).
    private static readonly Regex OwnerMoneyNote = new(
        @"AdvanceReceived|advanceReceived|AdvanceAboveFinal|advanceAboveFinal|ck_calendarentries_advance|Advance cannot be more than the final amount\.|advance against final", RegexOptions.Compiled);

    private static bool IsOwnerFile(string path)
        => path.Replace('\\', '/').Contains("/Owner/", StringComparison.Ordinal) || Path.GetFileName(path).StartsWith("Owner", StringComparison.Ordinal);

    private static string WithoutOwnerMoneyNote(string file, string line) => IsOwnerFile(file) ? OwnerMoneyNote.Replace(line, string.Empty) : line;

    [Fact]
    public void Production_source_has_no_advance_proof_or_hold_words_outside_the_allow_list()
    {
        var hits = TaxiSource.ProductionFiles()
            .SelectMany(f => File.ReadAllLines(f).Select((line, i) => (File: f, No: i + 1, Line: line)))
            .Where(x => Banned.IsMatch(WithoutOwnerMoneyNote(x.File, x.Line)) && !Allowed.Any(a => x.Line.Contains(a, StringComparison.Ordinal)))
            .Select(x => $"{Path.GetFileName(x.File)}:{x.No}: {x.Line.Trim()}")
            .ToList();
        Assert.True(hits.Count == 0, string.Join(Environment.NewLine, hits));
    }

    [Fact]
    public void Enquiry_and_offer_entities_hold_no_advance_hold_or_proof_property()
    {
        foreach (var t in new[] { typeof(TripEnquiry), typeof(TripOffer) })
        {
            var bad = t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.Name)
                .Where(n => n.Contains("Advance") || n.Contains("Hold") || n.Contains("Proof") || n.Contains("HostReminder"))
                .ToList();
            Assert.True(bad.Count == 0, $"{t.Name}: {string.Join(", ", bad)}");
        }
    }

    private static IEnumerable<string> ConstValues(Type t)
        => t.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!);

    [Fact]
    public void Notification_and_message_types_hold_no_advance_or_hold_value()
    {
        foreach (var v in ConstValues(typeof(NotificationTypes)).Concat(ConstValues(typeof(TripMessageTypes))))
        {
            Assert.DoesNotContain("advance", v, StringComparison.OrdinalIgnoreCase);
            Assert.False(Regex.IsMatch(v, @"(?i)hold(?!ay)") && !v.Equals(NotificationTypes.PlanOnHold, StringComparison.Ordinal),
                $"type value '{v}'");
        }
    }

    [Fact]
    public void Removed_types_are_gone()
    {
        var core = typeof(TripEnquiry).Assembly;
        var api = typeof(RentNearBy.Api.Handlers.TripEnquiryHandlers).Assembly;
        foreach (var name in new[]
        {
            "RentNearBy.Core.Models.TripAdvanceStatuses", "RentNearBy.Core.Models.TripAdvanceRules", "RentNearBy.Core.Models.TripAdvanceSplit",
            "RentNearBy.Core.Models.HostReminderSchedule", "RentNearBy.Core.Models.HostReminderTally",
            "RentNearBy.Core.DTOs.Requests.AskAdvanceRequest", "RentNearBy.Core.DTOs.Requests.UpdateStayAdvanceDetailsRequest",
            "RentNearBy.Core.DTOs.Responses.TripAdvanceDto", "RentNearBy.Core.DTOs.Responses.AdminTripAdvanceDto",
        })
            Assert.Null(core.GetType(name));
        Assert.Null(api.GetType("RentNearBy.Api.Handlers.TripAdvanceHandlers"));
        Assert.Null(api.GetType("RentNearBy.Api.Validators.AskAdvanceRequestValidator"));
        Assert.Null(typeof(TripFailReasons).GetField("HoldExpired"));
        Assert.Null(typeof(TripDisplayStatuses).GetField("HoldExpired"));
    }
}
