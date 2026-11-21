using System.Text.RegularExpressions;

namespace RentNearBy.Tests.TaxiRequests;

// P1 condition 2: every writer that can book a driver takes the driver row lock first. ConfirmedByBoth is the only legacy
// state that occupies a driver's days, so its writers are pinned here. A new writer fails the count and must be reviewed.
public class LegacyConfirmWritersGuardTests
{
    private static readonly Regex ConfirmedWrite = new(
        @"(?<![=!<>])=\s*TripEnquiryStatuses\.ConfirmedByBoth\b(?!\s*[=)?:])|SetProperty\([^)]*Status[^)]*,\s*TripEnquiryStatuses\.ConfirmedByBoth|SET\s+\\?""?Status\\?""?\s*=\s*'ConfirmedByBoth'",
        RegexOptions.Compiled);

    [Theory]
    [InlineData("e.Status = TripEnquiryStatuses.ConfirmedByBoth;", true)]
    [InlineData("Status = TripEnquiryStatuses.ConfirmedByBoth,", true)]
    [InlineData(".SetProperty(e => e.Status, TripEnquiryStatuses.ConfirmedByBoth)", true)]
    [InlineData("SET \"Status\" = 'ConfirmedByBoth'", true)]
    [InlineData("if (e.Status == TripEnquiryStatuses.ConfirmedByBoth)", false)]
    [InlineData("e.Status != TripEnquiryStatuses.ConfirmedByBoth", false)]
    [InlineData("var both = TripEnquiryStatuses.ConfirmedByBoth;", true)]
    [InlineData("public const string ConfirmedByBoth = \"ConfirmedByBoth\";", false)]
    public void Writer_pattern_sees_assignments_only(string code, bool isWrite) => Assert.Equal(isWrite, ConfirmedWrite.IsMatch(code));

    // Plain variables holding the constant for a read query are not writers.
    private static readonly Regex ReadAlias = new(@"var\s+\w+\s*=\s*TripEnquiryStatuses\.ConfirmedByBoth;", RegexOptions.Compiled);

    [Fact]
    public void Confirmed_by_both_is_assigned_in_exactly_two_known_places()
    {
        var writes = TaxiSource.ProductionFiles()
            .SelectMany(f => File.ReadAllLines(f).Select(line => (File: Path.GetFileName(f), Line: line)))
            .Where(x => ConfirmedWrite.IsMatch(x.Line) && !ReadAlias.IsMatch(x.Line))
            .Select(x => x.File)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(["TripEnquiryHandlers.cs", "TripLeadHandlers.cs"], writes);
    }

    [Fact]
    public void Offer_accept_cannot_confirm_a_local_taxi_row()
    {
        // AcceptOffer confirms only an Offered row ...
        var enquiry = TaxiSource.Read("RentNearBy.Api", "Handlers", "TripEnquiryHandlers.cs");
        var accept = TaxiSource.Member(enquiry, "public static async Task<IResult> AcceptOffer(");
        var guard = TaxiSource.At(accept, "e.Status != TripEnquiryStatuses.Offered");
        var write = TaxiSource.At(accept, "e.Status = TripEnquiryStatuses.ConfirmedByBoth;");
        Assert.True(guard < write);

        // ... and a row becomes Offered only through the host's SendOffer, which refuses every row that is not a stay.
        var offeredWriters = TaxiSource.ProductionFiles()
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"(?<![=!])=\s*TripEnquiryStatuses\.Offered;"))
            .Select(Path.GetFileName).ToList();
        Assert.Equal(["TripOwnerHandlers.cs"], offeredWriters);
        var owner = TaxiSource.Read("RentNearBy.Api", "Handlers", "TripOwnerHandlers.cs");
        var send = TaxiSource.Member(owner, "public static async Task<IResult> SendOffer(");
        var stayOnly = TaxiSource.At(send, "if (e.Kind != TripEnquiryKinds.Stay");
        var offered = TaxiSource.At(send, "e.Status = TripEnquiryStatuses.Offered;");
        Assert.True(stayOnly < offered);

        // The revised offer path never assigns ConfirmedByBoth itself (it works on a row that is already confirmed).
        var revised = TaxiSource.Member(enquiry, "private static async Task<IResult> AcceptRevisedAsync(");
        Assert.DoesNotMatch(ConfirmedWrite, revised);
    }
}
