using System.Text.RegularExpressions;
using RentNearBy.Api.Handlers;
using RentNearBy.Api.Handlers.TaxiRequests;

namespace RentNearBy.Tests.Account;

// Admin read API of DeletedAccountRecords: phone search, keyset paging, list never carries the licence, role shown.
public class AdminDeletedAccountRulesTests
{
    private static DeletedAccountRecord Record(DateTime deletedAt, string phone = "919876543210", string? name = "Rahul", string role = UserRoles.Seeker)
        => new() { Id = Guid.NewGuid(), OriginalUserId = Guid.NewGuid(), PhoneNumber = phone, Name = name, Role = role, DeletedAt = deletedAt, AccountCreatedAt = deletedAt.AddDays(-100) };

    [Fact]
    public void ParseSearch_digits_is_phone_search()
    {
        Assert.Equal(("919876543210", (string?)null, false), AdminDeletedAccountRules.ParseSearch(" +91 98765-43210 "));
    }

    [Fact]
    public void ParseSearch_text_is_name_search()
    {
        Assert.Equal(((string?)null, "rahul", false), AdminDeletedAccountRules.ParseSearch("  Rahul "));
    }

    [Fact]
    public void ParseSearch_blank_is_none()
    {
        Assert.Equal(((string?)null, (string?)null, false), AdminDeletedAccountRules.ParseSearch("   "));
        Assert.Equal(((string?)null, (string?)null, false), AdminDeletedAccountRules.ParseSearch(null));
    }

    [Fact]
    public void ParseSearch_over_100_is_too_long()
    {
        Assert.True(AdminDeletedAccountRules.ParseSearch(new string('a', 101)).TooLong);
        Assert.False(AdminDeletedAccountRules.ParseSearch(new string('a', 100)).TooLong);
    }

    [Fact]
    public void Filter_phone_matches_digits_and_name_matches_lower_case()
    {
        var rows = new[] { Record(DateTime.UtcNow, "919876543210", "Rahul"), Record(DateTime.UtcNow, "918888888888", "Asha") }.AsQueryable();
        Assert.Equal("Rahul", Assert.Single(AdminDeletedAccountRules.Filter(rows, "98765", null)).Name);
        Assert.Equal("Asha", Assert.Single(AdminDeletedAccountRules.Filter(rows, null, "ash")).Name);
        Assert.Equal(2, AdminDeletedAccountRules.Filter(rows, null, null).Count());
    }

    [Fact]
    public void Page_orders_newest_first_and_excludes_the_cursor_row()
    {
        var t = new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
        var rows = new List<DeletedAccountRecord> { Record(t), Record(t), Record(t.AddHours(1)), Record(t.AddHours(-1)), Record(t) };
        var expected = rows.OrderByDescending(r => r.DeletedAt).ThenBy(r => r.Id).Select(r => r.Id).ToList();

        var seen = new List<Guid>();
        (DateTime, Guid)? after = null;
        for (var guard = 0; guard < 10; guard++)
        {
            var page = AdminDeletedAccountRules.Page(rows.AsQueryable(), after, 2).ToList();
            var more = page.Count > 2;
            page = page.Take(2).ToList();
            seen.AddRange(page.Select(r => r.Id));
            if (!more) break;
            var cursor = AdminTaxiRequestRules.EncodeCursor(page[^1].DeletedAt, page[^1].Id);
            Assert.True(AdminTaxiRequestRules.TryDecodeCursor(cursor, out var at, out var id));
            after = (at, id);
        }

        Assert.Equal(expected, seen);
    }

    [Fact]
    public void ToDto_list_never_carries_the_licence()
    {
        var r = Record(DateTime.UtcNow, role: UserRoles.TaxiDriver);
        r.DrivingLicenceNumberEnc = "v1.cipher";
        r.DateOfBirth = new DateOnly(1990, 5, 1);

        var dto = AdminDeletedAccountRules.ToDto(r, null);

        Assert.Null(dto.DrivingLicenceNumber);
        Assert.True(dto.HasDrivingLicence);
        Assert.Equal("1990-05-01", dto.DateOfBirth);
    }

    [Fact]
    public void ToDto_detail_formats_the_licence()
    {
        var r = Record(DateTime.UtcNow, role: UserRoles.TaxiDriver);
        r.DrivingLicenceNumberEnc = "v1.cipher";
        var dto = AdminDeletedAccountRules.ToDto(r, "UK0720190001234");
        Assert.Equal(TaxiIdentityRules.FormatDl("UK0720190001234"), dto.DrivingLicenceNumber);
    }

    [Theory]
    [InlineData(UserRoles.Seeker, "Traveller")]
    [InlineData(UserRoles.StayOwner, "Stay Owner")]
    [InlineData(UserRoles.TaxiDriver, "Taxi Driver")]
    public void ToDto_maps_role_key_and_name(string key, string name)
    {
        var dto = AdminDeletedAccountRules.ToDto(Record(DateTime.UtcNow, role: key), null);
        Assert.Equal(key, dto.Role);
        Assert.Equal(name, dto.RoleName);
        Assert.False(dto.HasDrivingLicence);
    }

    [Fact]
    public void Endpoints_are_admin_only_and_the_old_handler_is_gone()
    {
        var root = RepoRoot();
        var endpoints = File.ReadAllText(Path.Combine(root, "RentNearBy.Api", "Endpoints", "AdminEndpoints.cs"));
        Assert.Matches(new Regex(@"MapGet\(""/deleted-accounts"", AdminDeletedAccountHandlers\.GetDeletedAccounts\)\.RequireAuthorization\(""AdminOnly""\);"), endpoints);
        Assert.Matches(new Regex(@"MapGet\(""/deleted-accounts/\{id:guid\}"", AdminDeletedAccountHandlers\.GetDeletedAccount\)\.RequireAuthorization\(""AdminOnly""\);"), endpoints);
        Assert.Null(typeof(AdminHandlers).GetMethod("GetDeletedAccounts"));
    }

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(thisFile)! })
        {
            var dir = new DirectoryInfo(start);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RentNearBy.sln"))) dir = dir.Parent;
            if (dir != null) return dir.FullName;
        }

        throw new InvalidOperationException("Repository root not found");
    }
}
