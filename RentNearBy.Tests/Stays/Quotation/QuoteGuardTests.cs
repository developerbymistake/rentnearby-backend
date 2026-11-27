using System.Text.RegularExpressions;
using RentNearBy.Api.Endpoints.Owner;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Tests.SeekerOnly;

namespace RentNearBy.Tests.Stays.Quotation;

// Source and route guards of the quotation feature: S7 logging, Q21 routes without ids under CanListStays, the own buckets and
// their order, no BankEnc copy into DeletedAccountRecords, no server PDF library, the plan key never typed as a number.
public class QuoteGuardTests
{
    private static string Root => RentNearBy.Tests.Trips.CompletionCounterGuardTests.Root();

    private static string Src(params string[] parts) => File.ReadAllText(Path.Combine([Root, .. parts]));

    private static IEnumerable<string> Sources(string project)
        => Directory.EnumerateFiles(Path.Combine(Root, project), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"));

    // S7 / A-13: no HTTP body logging exists; adding it must exclude the quotation path (bank values in PUT /profile bodies).
    [Fact]
    public void No_http_body_logging_unless_the_quotation_path_is_excluded()
    {
        foreach (var file in Sources("RentNearBy.Api"))
        {
            var text = File.ReadAllText(file);
            if (!Regex.IsMatch(text, @"AddHttpLogging|UseHttpLogging|EnableBuffering\(")) continue;
            Assert.True(text.Contains("/api/v1/stays/me/quotation", StringComparison.Ordinal),
                $"{Path.GetFileName(file)} logs or buffers request bodies without excluding /api/v1/stays/me/quotation");
        }
    }

    [Fact]
    public void Quotation_code_never_logs_a_request_or_a_bank_value()
    {
        var files = Sources("RentNearBy.Api").Concat(Sources("RentNearBy.Infrastructure"))
            .Where(f => f.Contains("Quot", StringComparison.Ordinal) || f.Contains($"{Path.DirectorySeparatorChar}Quote{Path.DirectorySeparatorChar}"));
        foreach (var file in files)
        {
            foreach (var line in File.ReadLines(file).Where(l => l.Contains("Log", StringComparison.Ordinal) && Regex.IsMatch(l, @"\.Log(Information|Warning|Error|Debug|Trace|Critical)\(")))
            {
                Assert.DoesNotMatch(@"request|Request\b|AccountNo|Holder|Upi\b|Ifsc|bank\.|plain", line);
            }
        }
    }

    [Fact]
    public void Deleted_account_record_never_copies_the_bank_blob()
    {
        var src = Src("RentNearBy.Infrastructure", "Services", "AccountDeletionService.cs");
        Assert.DoesNotContain("BankEnc", src);
        Assert.DoesNotContain(typeof(DeletedAccountRecord).GetProperties(), p => p.Name.Contains("Bank", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_quotation_route_is_owner_only_and_takes_no_id()
    {
        var quote = SeekerRouteTable.Routes.Where(r => r.Key.Contains("/api/v1/stays/me/quotation", StringComparison.Ordinal)).ToList();
        Assert.Equal(11, quote.Count);
        Assert.All(quote, r =>
        {
            Assert.Equal(["CanListStays"], r.Value.Policies);
            Assert.False(r.Value.AllowAnonymous);
            Assert.DoesNotMatch(@"\{\w*id\w*(:guid)?\}", r.Key.ToLowerInvariant());
        });
    }

    [Fact]
    public void Writes_take_their_buckets_and_the_own_bucket_runs_first()
    {
        var src = Src("RentNearBy.Api", "Endpoints", "Owner", "StayQuotationEndpoints.cs");
        Assert.Contains("StayQuotationHandlers.Reserve).QuoteReserveLimited()", src);
        Assert.Contains("StayQuotationHandlers.RequestNameChange).QuoteNameLimited()", src);
        Assert.Contains("StayQuotationHandlers.PutLogo).DisableAntiforgery().QuotePhotoLimited().PhotoBodyLimited()", src);
        Assert.Contains("StayQuotationHandlers.PutSignature).DisableAntiforgery().QuotePhotoLimited().PhotoBodyLimited()", src);
        foreach (var line in src.Split('\n').Where(l => Regex.IsMatch(l, "Map(Put|Post|Delete)\\(")))
            Assert.Matches("OwnerWriteLimited\\(\\)|QuotePhotoLimited\\(\\)|QuoteReserveLimited\\(\\)|QuoteNameLimited\\(\\)", line);
        var filters = Src("RentNearBy.Api", "Filters", "QuoteLimitFilters.cs");
        Assert.Contains("AddEndpointFilter<QuoteReserveLimitFilter>().OwnerWriteLimited()", filters);
        Assert.DoesNotContain("Release", filters);
        Assert.NotNull(typeof(StayQuotationEndpoints));
    }

    // Q22: the PDF is made on the phone; the Backend references no PDF or HTML renderer.
    [Fact]
    public void Backend_has_no_pdf_library()
    {
        foreach (var project in new[] { "RentNearBy.Api", "RentNearBy.Core", "RentNearBy.Infrastructure" })
        {
            var csproj = Src(project, project + ".csproj");
            Assert.DoesNotMatch("(?i)selectpdf|questpdf|wkhtml|puppeteer|playwright|itext|pdfsharp", csproj);
        }
    }

    // BUILD_PLAN 4.3: never a typed 2 / 20 / 30 for the cap in the quotation code; the number comes from the plan limits.
    [Fact]
    public void The_monthly_cap_is_read_from_the_limits()
    {
        var reserve = Src("RentNearBy.Infrastructure", "Services", "Owner", "Quote", "StayQuoteReserveService.cs");
        Assert.Contains("limits.StayQuotationsPerMonth", reserve);
        Assert.DoesNotMatch(@"limit\s*=\s*\d", reserve);
    }

    [Fact]
    public void Image_store_is_the_photo_service()
        => Assert.Contains(typeof(RentNearBy.Infrastructure.Services.Owner.Quote.IQuoteImageStore), typeof(PhotoService).GetInterfaces());

    // M-1: the stored month is current when it is the same OR newer; the key only moves forward (PG proof:
    // QuoteReserveWindowTests.Month_key_never_moves_backwards_at_the_ist_edge).
    [Fact]
    public void Month_key_never_moves_backwards()
    {
        var sql = RentNearBy.Infrastructure.Services.Owner.Quote.StayQuoteReserveService.DecideSql;
        Assert.Contains("\"MonthKey\" = GREATEST(c.\"MonthKey\", $4)", sql);
        Assert.DoesNotContain("\"MonthKey\" = $4", sql);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(sql, "\"MonthKey\" >= \\$4").Count);
    }
}
