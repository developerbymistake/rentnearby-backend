namespace RentNearBy.Tests.Stays;

public class StayRegistrationRulesTests
{
    // Independent re-implementation of the GSTIN check character (Luhn mod 36, factors 1,2,1,2,... from the left),
    // so the production method is checked against the algorithm and against the literal expected character.
    private static char ReferenceCheckChar(string first14)
    {
        const string alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        var sum = 0;
        for (var i = 0; i < 14; i++)
        {
            var product = alphabet.IndexOf(first14[i]) * (i % 2 == 0 ? 1 : 2);
            sum += product / 36 + product % 36;
        }
        return alphabet[(36 - sum % 36) % 36];
    }

    [Theory]
    [InlineData("27AAPFU0939F1Z", 'V')]   // published example, sum 221
    [InlineData("05ABCDE1234F1Z", '6')]   // sum 174
    [InlineData("29ABCDE1234F1Z", 'W')]   // sum 184
    [InlineData("22AAAAA0000A1Z", 'C')]   // sum 132
    public void GstinCheckChar_MatchesAlgorithmAndLiteral(string first14, char expected)
    {
        Assert.Equal(expected, ReferenceCheckChar(first14));
        Assert.Equal(expected, StayRegistrationRules.GstinCheckChar(first14));
    }

    [Theory]
    [InlineData("27AAPFU0939F1ZV", true)]
    [InlineData("05ABCDE1234F1Z6", true)]
    [InlineData("29ABCDE1234F1ZW", true)]
    [InlineData("22AAAAA0000A1ZC", true)]
    [InlineData(" 05abcde1234f1z6 ", true)]
    [InlineData("29ABCDE1234F1Z5", false)]   // checksum
    [InlineData("22AAAAA0000A1Z5", false)]   // GST tutorial sample, checksum
    [InlineData("00ABCDE1234F1Z6", false)]   // state 00
    [InlineData("39ABCDE1234F1Z6", false)]   // state 39
    [InlineData("05ABCDE1234F1Y6", false)]   // no Z
    [InlineData("05ABCDE1234F0Z6", false)]   // entity 0
    [InlineData("05ABCDE1234F1Z", false)]    // 14 characters
    [InlineData("", false)]
    public void IsValidGstin_Vectors(string raw, bool valid)
        => Assert.Equal(valid, StayRegistrationRules.IsValidGstin(StayRegistrationRules.NormalizeGstin(raw)));

    [Fact]
    public void NormalizeGstin_TrimsUppercasesAndRemovesSpaces()
        => Assert.Equal("05ABCDE1234F1Z6", StayRegistrationRules.NormalizeGstin(" 05abcde 1234f1z6 "));

    [Theory]
    [InlineData("UK/HS/2021/0457", "UK/HS/2021/0457", true)]
    [InlineData("hs 2021 0457", "HS20210457", true)]
    [InlineData("ABCDE", "ABCDE", false)]          // no digit
    [InlineData("A1", "A1", false)]                // too short
    [InlineData("UK#2021", "UK#2021", false)]      // bad character
    [InlineData("1234567890123456789012345", "1234567890123456789012345", true)]   // 25 characters
    [InlineData("12345678901234567890123456", "12345678901234567890123456", false)] // 26 characters
    public void Registration_Generic(string raw, string normalised, bool valid)
    {
        Assert.Equal(normalised, StayRegistrationRules.NormalizeRegistration(raw));
        Assert.Equal(valid, StayRegistrationRules.IsValidRegistration(normalised));
    }

    [Theory]
    [InlineData("udyam-uk-05-0001234")]
    [InlineData("UDYAM UK 05 0001234")]
    [InlineData("UDYAMUK050001234")]
    [InlineData("UDYAM-UK-05-0001234")]
    public void Udyam_Variants_CanonicalAndValid(string raw)
    {
        var canonical = StayRegistrationRules.NormalizeForType(StayRegistrationRules.UdyamCode, raw);
        Assert.Equal("UDYAM-UK-05-0001234", canonical);
        Assert.True(StayRegistrationRules.IsValidForType(StayRegistrationRules.UdyamCode, canonical));
    }

    [Theory]
    [InlineData("UDYAM-XX-05-0001234")]   // unknown state
    [InlineData("UDYAM-UK-05-001234")]    // six digits
    [InlineData("UAM-UK-05-0001234")]     // Udyog Aadhaar style
    public void Udyam_Invalid(string raw)
    {
        var n = StayRegistrationRules.NormalizeForType(StayRegistrationRules.UdyamCode, raw);
        Assert.False(StayRegistrationRules.IsValidForType(StayRegistrationRules.UdyamCode, n));
    }

    [Fact]
    public void IsValidForType_NonUdyamTypeUsesGenericRule()
    {
        Assert.True(StayRegistrationRules.IsValidForType("UK_HOMESTAY", "UK/HS/2021/0457"));
        Assert.True(StayRegistrationRules.IsValidForType("TOURISM", "UAM-UK-05-0001234"));
        Assert.Equal("UAM-UK-05-0001234", StayRegistrationRules.NormalizeForType("TOURISM", "uam-uk-05-0001234"));
    }

    [Fact]
    public void MessageForType_UdyamOrGeneric()
    {
        Assert.Equal("Enter a valid Udyam number, e.g. UDYAM-UK-05-0001234", StayRegistrationRules.MessageForType("UDYAM"));
        Assert.Equal("Enter a valid registration number", StayRegistrationRules.MessageForType("OTHER"));
    }

    [Theory]
    [InlineData("  Gram   Panchayat NOC ", "Gram Panchayat NOC", true)]
    [InlineData("NOC", "NOC", true)]
    [InlineData("AB", "AB", false)]
    [InlineData("Fire NOC (2024) & Co.", "Fire NOC (2024) & Co.", true)]
    [InlineData("Licence #5", "Licence #5", false)]
    [InlineData("", "", false)]
    public void OtherName_Rules(string raw, string normalised, bool valid)
    {
        Assert.Equal(normalised, StayRegistrationRules.NormalizeOtherName(raw));
        Assert.Equal(valid, StayRegistrationRules.IsValidOtherName(normalised));
    }

    [Fact]
    public void OtherName_FortyOneCharacters_Invalid()
        => Assert.False(StayRegistrationRules.IsValidOtherName(new string('A', 41)));

    [Theory]
    [InlineData("Uttarakhand Homestay", "UTTARAKHAND_HOMESTAY")]
    [InlineData("Uttarakhand Homestay registration", "UTTARAKHAND_HOMESTAY_REGISTRATIO")]   // 33 -> 32
    [InlineData("Tourism / hotel registration", "TOURISM_HOTEL_REGISTRATION")]
    [InlineData("  Fire NOC ", "FIRE_NOC")]
    [InlineData("--", "")]
    public void CodeOf_UpperSnake(string name, string code)
        => Assert.Equal(code, StayRegistrationRules.CodeOf(name));

    [Fact]
    public void CodeOf_CappedAt32()
        => Assert.True(StayRegistrationRules.CodeOf(new string('A', 50)).Length <= 32);

    [Theory]
    [InlineData("e2000000-0000-0000-0000-000000000001", true)]
    [InlineData("e2000000-0000-0000-0000-000000000005", true)]
    [InlineData("e1000000-0000-0000-0000-000000000001", false)]
    public void Seed_IsSeeded(string id, bool seeded)
        => Assert.Equal(seeded, StayRegistrationTypeSeed.IsSeeded(Guid.Parse(id)));
}
