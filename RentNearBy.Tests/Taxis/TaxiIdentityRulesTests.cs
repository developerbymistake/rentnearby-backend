namespace RentNearBy.Tests.Taxis;

public class TaxiIdentityRulesTests
{
    private const int CurrentYear = 2026;
    private static readonly DateOnly Today = DateOnly.FromDateTime(new FakeClock().UtcNow);   // 2026-10-15

    private static DateOnly D(string iso) => DateOnly.ParseExact(iso, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("UK07 20150012345", "UK0720150012345")]
    [InlineData("uk-07-2015-0012345", "UK0720150012345")]
    [InlineData(" UK07/2015/0012345 ", "UK0720150012345")]
    [InlineData("UK07.2015.0012345", "UK0720150012345")]
    [InlineData(null, "")]
    public void NormalizeDl_StripsSeparatorsAndUppercases(string? raw, string expected)
        => Assert.Equal(expected, TaxiIdentityRules.NormalizeDl(raw));

    [Theory]
    [InlineData("UK07 20150012345", true)]
    [InlineData("uk-07-2015-0012345", true)]
    [InlineData("UA0719990012345", true)]    // legacy Uttaranchal code
    [InlineData("TG0520240012345", true)]    // Telangana 2024 code
    [InlineData("MH0420100034761", true)]
    [InlineData("XX0720150012345", false)]   // unknown state
    [InlineData("UK0020150012345", false)]   // RTO 00
    [InlineData("UK0719690012345", false)]   // year before 1970
    [InlineData("UK0720270012345", false)]   // future year
    [InlineData("UK0720150000000", false)]   // zero serial
    [InlineData("UK07 15 0012345", false)]   // two-digit year
    [InlineData("UK07201500123456", false)]  // 16 characters
    [InlineData("", false)]
    public void IsValidDl_Vectors(string raw, bool valid)
        => Assert.Equal(valid, TaxiIdentityRules.IsValidDl(TaxiIdentityRules.NormalizeDl(raw), CurrentYear));

    [Fact]
    public void IsValidDl_YearEqualToCurrent_Valid()
        => Assert.True(TaxiIdentityRules.IsValidDl("UK0720260012345", CurrentYear));

    [Fact]
    public void IsValidDl_YearIsMinYear_Valid()
        => Assert.True(TaxiIdentityRules.IsValidDl("UK0719700012345", CurrentYear));

    [Fact]
    public void DlIssueYear_ReadsYearDigits()
        => Assert.Equal(2015, TaxiIdentityRules.DlIssueYear("UK0720150012345"));

    [Fact]
    public void FormatDl_SpaceAfterRtoCode()
        => Assert.Equal("UK07 20150012345", TaxiIdentityRules.FormatDl("UK0720150012345"));

    [Fact]
    public void StateCodes_HoldCurrentAndLegacyCodes()
    {
        Assert.Equal(40, IndianStateCodes.All.Length);
        Assert.Equal(IndianStateCodes.All.Length, IndianStateCodes.All.Distinct().Count());
        foreach (var code in new[] { "UK", "UA", "OR", "OD", "DN", "TS", "TG", "LA", "DL" })
            Assert.True(IndianStateCodes.Contains(code), code);
    }

    [Theory]
    [InlineData("1990-05-14", true)]
    [InlineData(" 1990-05-14 ", true)]
    [InlineData("14/05/1990", false)]
    [InlineData("1990-5-14", false)]
    [InlineData("1990-02-30", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void TryParseDob_OnlyIsoCalendarDates(string? value, bool ok)
        => Assert.Equal(ok, TaxiIdentityRules.TryParseDob(value, out _));

    [Theory]
    [InlineData("2008-10-15", 18, DobStatus.Ok)]
    [InlineData("2008-10-16", 17, DobStatus.Minor)]
    [InlineData("1926-10-15", 100, DobStatus.Ok)]
    [InlineData("1926-10-14", 100, DobStatus.Ok)]
    [InlineData("1925-10-16", 100, DobStatus.Ok)]
    [InlineData("1925-10-15", 101, DobStatus.TooOld)]
    [InlineData("1990-05-14", 36, DobStatus.Ok)]
    public void AgeAndStatus_OnFakeClockToday(string dob, int age, DobStatus status)
    {
        Assert.Equal(new DateOnly(2026, 10, 15), Today);
        Assert.Equal(age, TaxiIdentityRules.AgeOn(D(dob), Today));
        Assert.Equal(status, TaxiIdentityRules.GetDobStatus(D(dob), Today));
    }

    [Fact]
    public void GetDobStatus_Tomorrow_Future()
        => Assert.Equal(DobStatus.Future, TaxiIdentityRules.GetDobStatus(D("2026-10-16"), Today));

    [Theory]
    [InlineData("2026-02-28", 17)]
    [InlineData("2026-03-01", 18)]
    [InlineData("2028-02-29", 20)]
    public void AgeOn_LeapDayBirthday(string today, int age)
        => Assert.Equal(age, TaxiIdentityRules.AgeOn(D("2008-02-29"), D(today)));

    [Theory]
    [InlineData(2018, true)]
    [InlineData(2017, false)]
    public void DlYearMatchesDob_IssueYearAtLeastBirthYearPlus18(int issueYear, bool ok)
        => Assert.Equal(ok, TaxiIdentityRules.DlYearMatchesDob(issueYear, D("2000-06-01")));

    [Fact]
    public void Messages_AreTheAgreedCopy()
    {
        Assert.Equal("Enter a valid licence number", TaxiIdentityRules.DlInvalidMessage);
        Assert.Equal("You must be 18 or older", TaxiIdentityRules.DobMinorMessage);
        Assert.Equal("Enter a valid date of birth", TaxiIdentityRules.DobInvalidMessage);
        Assert.Equal("Licence year does not match your date of birth", TaxiIdentityRules.DlYearMessage);
    }
}
