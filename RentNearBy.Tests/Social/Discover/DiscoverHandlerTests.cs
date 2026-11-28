using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using RentNearBy.Api.Handlers.Social;
using RentNearBy.Core.DTOs.Social;
using RentNearBy.Core.Interfaces.Social;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Services.Social;

namespace RentNearBy.Tests.Social.Discover;

// GET /social/discover with fakes (DESIGN 10.1): chip and cursor checks, the Plus-only Near you chip, limit and refresh.
public class DiscoverHandlerTests
{
    private sealed class FakeDiscover : IDiscoverService
    {
        public List<(string Chip, DiscoverCursorValue? Cursor, int Limit, bool Refresh)> Pages { get; } = [];
        public List<int> SimilarLimits { get; } = [];

        public Task<DiscoverPageDto> PageAsync(Guid me, string chip, DiscoverCursorValue? cursor, int limit, bool refresh, CancellationToken ct = default)
        {
            Pages.Add((chip, cursor, limit, refresh));
            return Task.FromResult(new DiscoverPageDto());
        }

        public Task HideAsync(Guid me, Guid him, CancellationToken ct = default) => Task.CompletedTask;

        public Task<DiscoverSimilarDto> SimilarAsync(Guid me, Guid him, int limit, CancellationToken ct = default)
        {
            SimilarLimits.Add(limit);
            return Task.FromResult(new DiscoverSimilarDto());
        }
    }

    private sealed class CountingLimits(SeekerLimits limits) : ISeekerLimits
    {
        public int Calls { get; private set; }

        public Task<SeekerLimits> GetAsync(Guid userId, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(limits);
        }
    }

    private sealed class AllowAll : IRateLimitService
    {
        public Task<RateLimitResult> CheckAsync(string key, int maxAttempts, TimeSpan window, int permits = 1)
            => Task.FromResult(new RateLimitResult(true, maxAttempts - 1, null, "t"));

        public Task ReleaseAsync(string key, string token) => Task.CompletedTask;
    }

    private static ClaimsPrincipal User() => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "test"));

    private static SeekerLimits Free() => new(false, true, 1, 1, 1, 3, false, false, false);

    private static SeekerLimits Plus() => new(true, false, 6, 3, 5, 10, true, true, true);

    private static async Task<(int Status, JsonElement Body)> RunAsync(IResult result)
    {
        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        http.Response.Body = new MemoryStream();
        await result.ExecuteAsync(http);
        http.Response.Body.Position = 0;
        var text = await new StreamReader(http.Response.Body).ReadToEndAsync();
        return (http.Response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement);
    }

    private static async Task<(int Status, JsonElement Body)> Discover(FakeDiscover fake, ISeekerLimits limits, string? chip = null,
        string? cursor = null, int? limit = null, string? refresh = null)
        => await RunAsync(await DiscoverHandlers.Discover(User(), fake, limits, new AllowAll(), new DefaultHttpContext(), default,
            chip, cursor, limit, refresh));

    [Fact]
    public async Task Free_user_on_the_near_chip_gets_403_plus_required_and_no_page()
    {
        var fake = new FakeDiscover();
        var (status, body) = await Discover(fake, new CountingLimits(Free()), DiscoverChips.Near);
        Assert.Equal(403, status);
        Assert.Equal(SocialErrorTypes.PlusRequired, body.GetProperty("error").GetProperty("type").GetString());
        Assert.Empty(fake.Pages);
    }

    [Fact]
    public async Task Plus_user_on_the_near_chip_gets_200()
    {
        var fake = new FakeDiscover();
        var (status, _) = await Discover(fake, new CountingLimits(Plus()), DiscoverChips.Near);
        Assert.Equal(200, status);
        Assert.Equal(DiscoverChips.Near, Assert.Single(fake.Pages).Chip);
    }

    [Fact]
    public async Task For_you_never_reads_the_plan_so_free_users_get_near_people_there()
    {
        var fake = new FakeDiscover();
        var limits = new CountingLimits(Free());
        var (status, _) = await Discover(fake, limits);
        Assert.Equal(200, status);
        Assert.Equal(0, limits.Calls);
        var page = Assert.Single(fake.Pages);
        Assert.Equal(DiscoverChips.ForYou, page.Chip);
        Assert.Equal(SocialLimits.DiscoverPage, page.Limit);
        Assert.False(page.Refresh);
    }

    [Fact]
    public async Task A_bad_chip_is_400_with_the_pick_message()
    {
        var fake = new FakeDiscover();
        var (status, body) = await Discover(fake, new CountingLimits(Plus()), "everyone");
        Assert.Equal(400, status);
        Assert.Equal(DiscoverChips.BadChipMessage, body.GetProperty("error").GetProperty("message").GetString());
        Assert.Empty(fake.Pages);
    }

    [Fact]
    public async Task A_bad_cursor_is_400()
    {
        var fake = new FakeDiscover();
        var (status, _) = await Discover(fake, new CountingLimits(Plus()), cursor: "not-a-cursor!");
        Assert.Equal(400, status);
        Assert.Empty(fake.Pages);
    }

    [Fact]
    public async Task Cursor_limit_and_refresh_reach_the_service()
    {
        var fake = new FakeDiscover();
        var id = Guid.NewGuid();
        var cursor = DiscoverCursor.Encode(123, 40, id);
        await Discover(fake, new CountingLimits(Plus()), DiscoverChips.Mutual, cursor, 500, "1");
        var page = Assert.Single(fake.Pages);
        Assert.Equal(new DiscoverCursorValue(123, 40, id), page.Cursor);
        Assert.Equal(SocialLimits.DiscoverPage, page.Limit);
        Assert.True(page.Refresh);
        await Discover(fake, new CountingLimits(Plus()), limit: 5);
        Assert.Equal(5, fake.Pages[1].Limit);
    }

    [Fact]
    public async Task Similar_limit_defaults_to_5_and_caps_at_10()
    {
        var fake = new FakeDiscover();
        await RunAsync(await DiscoverHandlers.Similar(Guid.NewGuid(), User(), fake, new AllowAll(), new DefaultHttpContext(), default));
        await RunAsync(await DiscoverHandlers.Similar(Guid.NewGuid(), User(), fake, new AllowAll(), new DefaultHttpContext(), default, 99));
        Assert.Equal([SocialLimits.DiscoverSimilarDefault, SocialLimits.DiscoverSimilarMax], fake.SimilarLimits);
    }

    [Fact]
    public async Task No_caller_is_401()
    {
        var fake = new FakeDiscover();
        var result = await DiscoverHandlers.Discover(new ClaimsPrincipal(new ClaimsIdentity()), fake, new CountingLimits(Plus()), new AllowAll(),
            new DefaultHttpContext(), default);
        Assert.Equal(401, (await RunAsync(result)).Status);
        Assert.Empty(fake.Pages);
    }
}
