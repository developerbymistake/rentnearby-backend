namespace RentNearBy.Tests.Users;

public class UsernamesTests
{
    [Fact]
    public void Format_is_prefix_plus_eight_digits()
    {
        Assert.Equal("Bakhli#48291736", Usernames.Format(48291736));
        Assert.Equal("Bakhli#10000000", Usernames.Format(Usernames.MinNumber));
        Assert.Equal("Bakhli#99999999", Usernames.Format(Usernames.MaxNumberExclusive - 1));
    }

    [Theory]
    [InlineData(9_999_999)]
    [InlineData(100_000_000)]
    [InlineData(0)]
    [InlineData(-1)]
    public void Format_rejects_numbers_outside_eight_digits_with_leading_one_to_nine(int number)
        => Assert.Throws<ArgumentOutOfRangeException>(() => Usernames.Format(number));

    [Theory]
    [InlineData("Bakhli#48291736", 48291736)]
    [InlineData("Bakhli#10000000", 10000000)]
    public void TryParse_reads_valid_usernames(string text, int expected)
    {
        Assert.True(Usernames.TryParse(text, out var number));
        Assert.Equal(expected, number);
        Assert.True(Usernames.IsValid(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bakhli#")]
    [InlineData("Bakhli#01234567")]
    [InlineData("Bakhli#1234567")]
    [InlineData("Bakhli#123456789")]
    [InlineData("bakhli#12345678")]
    [InlineData("Bakhli#1234567a")]
    [InlineData("Bakhli 12345678")]
    [InlineData(" Bakhli#12345678")]
    [InlineData("Bakhli#١٢٣٤٥٦٧٨")]
    public void TryParse_rejects_anything_else(string? text)
    {
        Assert.False(Usernames.TryParse(text, out var number));
        Assert.Equal(0, number);
    }

    [Fact]
    public void Username_fits_the_column()
        => Assert.True(Usernames.Format(Usernames.MaxNumberExclusive - 1).Length <= Usernames.MaxLength);

    [Fact]
    public void NewRandom_is_always_valid_and_covers_every_first_digit()
    {
        var firstDigits = new HashSet<char>();
        for (var i = 0; i < 5000; i++)
        {
            var name = Usernames.NewRandom();
            Assert.True(Usernames.IsValid(name), name);
            firstDigits.Add(name[Usernames.Prefix.Length]);
        }
        Assert.Equal("123456789", string.Concat(firstDigits.OrderBy(c => c)));
    }

    [Fact]
    public async Task AssignAsync_retries_on_clash_and_returns_the_stored_name()
    {
        var tried = new List<string>();
        var next = Sequence("Bakhli#11111111", "Bakhli#22222222", "Bakhli#33333333");
        var result = await Usernames.AssignAsync(c =>
        {
            tried.Add(c);
            return Task.FromResult<string?>(tried.Count < 3 ? null : c);
        }, next);

        Assert.Equal("Bakhli#33333333", result);
        Assert.Equal(["Bakhli#11111111", "Bakhli#22222222", "Bakhli#33333333"], tried);
    }

    [Fact]
    public async Task AssignAsync_tries_a_valid_preferred_name_first()
    {
        var tried = new List<string>();
        var result = await Usernames.AssignAsync(c => { tried.Add(c); return Task.FromResult<string?>(c); },
            Sequence("Bakhli#22222222"), "Bakhli#48291736");

        Assert.Equal("Bakhli#48291736", result);
        Assert.Equal(["Bakhli#48291736"], tried);
    }

    [Fact]
    public async Task AssignAsync_ignores_an_invalid_preferred_name()
    {
        var result = await Usernames.AssignAsync(c => Task.FromResult<string?>(c),
            Sequence("Bakhli#22222222"), "Bakhli#0");
        Assert.Equal("Bakhli#22222222", result);
    }

    [Fact]
    public async Task AssignAsync_gives_up_after_six_clashes_with_a_clear_error()
    {
        var calls = 0;
        var ex = await Assert.ThrowsAsync<UsernameUnavailableException>(() => Usernames.AssignAsync(
            _ => { calls++; return Task.FromResult<string?>(null); }, Usernames.NewRandom));

        Assert.Equal(Usernames.MaxAttempts, calls);
        Assert.Equal(6, Usernames.MaxAttempts);
        Assert.Contains("unique username", ex.Message);
    }

    internal static Func<string> Sequence(params string[] names)
    {
        var queue = new Queue<string>(names);
        return () => queue.Dequeue();
    }
}
