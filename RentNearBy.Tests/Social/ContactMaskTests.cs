using RentNearBy.Core.Models.Social;

namespace RentNearBy.Tests.Social;

// Contract 3.3: server-side masking of phone numbers, emails, links and handles.
public class ContactMaskTests
{
    [Theory]
    [InlineData("call me 9876543210")]
    [InlineData("call me +91 98765 43210")]
    [InlineData("98-765-43210 anytime")]
    [InlineData("(98) 765.432.10")]
    [InlineData("९८७६५४३२१० whatsapp")]
    [InlineData("９８７６５４３２１０")]
    [InlineData("9o87654321")]
    [InlineData("nine eight seven six five four three")]
    [InlineData("nau aath saat chhe paanch char")]
    [InlineData("mail me abc@gmail.com")]
    [InlineData("abc (at) gmail dot com")]
    [InlineData("abc at the rate gmail dot com")]
    [InlineData("see https://example.org/x")]
    [InlineData("www.mysite.in")]
    [InlineData("wa.me/919876543210")]
    [InlineData("t.me/aman")]
    [InlineData("instagram.com/aman_travels")]
    [InlineData("dm me @aman_travels")]
    public void Contact_details_are_masked(string input)
    {
        var (text, masked) = ContactMask.Apply(input);
        Assert.True(masked, input);
        Assert.Contains(ContactMask.Mark, text);
    }

    [Theory]
    [InlineData("Leaving at 6 AM")]
    [InlineData("Back on 12 Dec")]
    [InlineData("Trip in 2026")]
    [InlineData("Budget ₹2,500 each")]
    [InlineData("Kedarnath is 3,583 m and Tungnath 5,364 m")]
    [InlineData("Only 10 km a day")]
    [InlineData("Day 2 is the trek")]
    [InlineData("Taking NH 44 north")]
    public void Ordinary_numbers_pass_untouched(string input)
    {
        var (text, masked) = ContactMask.Apply(input);
        Assert.False(masked, input);
        Assert.Equal(input, text);
    }

    [Fact]
    public void Adjacent_matches_collapse_to_one_mark()
    {
        var (text, _) = ContactMask.Apply("9876543210 abc@gmail.com");
        Assert.Equal(ContactMask.Mark, text);
    }

    [Fact]
    public void Invisible_characters_are_stripped_from_the_stored_text_too()
    {
        var (text, masked) = ContactMask.Apply("98​76543210 ok");
        Assert.True(masked);
        Assert.DoesNotContain('​', text);
        Assert.EndsWith(" ok", text);
    }

    [Fact]
    public void Only_marks_is_detected()
    {
        Assert.True(ContactMask.IsOnlyMarks(ContactMask.Apply("9876543210").Text));
        Assert.False(ContactMask.IsOnlyMarks(ContactMask.Apply("call 9876543210 tomorrow").Text));
    }

    [Theory]
    [InlineData("Room 1234567", true)]
    [InlineData("98-76-54-32-10", true)]
    [InlineData("NH 44", false)]
    [InlineData("Manali", false)]
    public void Digit_runs_in_names_and_places_are_found(string input, bool expected)
        => Assert.Equal(expected, ContactMask.HasDigitRun(input));

    [Fact]
    public void Empty_input_is_empty() => Assert.Equal((string.Empty, false), ContactMask.Apply(null));
}
