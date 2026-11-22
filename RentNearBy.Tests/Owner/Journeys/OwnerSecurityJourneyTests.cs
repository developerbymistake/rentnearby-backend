using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using RentNearBy.Api.Handlers.Owner;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Tests.Trips;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Tests.Owner.Journeys;

// JT-B-SEC-1: owner write limits (FB-08, FB-11, FB-52) and the job broadcast dedupe (FB-08).
public class OwnerSecurityJourneyTests
{
    private static string Src(params string[] parts) => File.ReadAllText(Path.Combine([CompletionCounterGuardTests.Root(), .. parts]));

    private static int Status(IResult r) => (r as IStatusCodeHttpResult)?.StatusCode ?? 200;

    [Theory]
    [InlineData("StayEndpoints.cs")]
    [InlineData("TaxiEndpoints.cs")]
    public void Every_me_write_route_is_write_limited(string file)
    {
        var lines = Src("RentNearBy.Api", "Endpoints", file).Split('\n')
            .Where(l => Regex.IsMatch(l, "Map(Put|Post|Delete)\\(\"/me"));
        Assert.NotEmpty(lines);
        Assert.All(lines, l => Assert.Matches("OwnerWriteLimited\\(\\)|OwnerPhotoLimited\\(\\)", l));
    }

    [Fact]
    public void Photo_uploads_take_the_hourly_photo_bucket()
    {
        Assert.Contains("StayHandlers.UploadPhoto).RequireAuthorization(\"CanListStays\").DisableAntiforgery().OwnerPhotoLimited()",
            Src("RentNearBy.Api", "Endpoints", "StayEndpoints.cs"));
        Assert.Equal((20, TimeSpan.FromHours(1)), (OwnerRateLimitKeys.Photo(Guid.Empty).Max, OwnerRateLimitKeys.Photo(Guid.Empty).Window));
    }

    [Fact]
    public void Stay_job_writes_are_limited_and_never_publish_to_seekers_directly()
    {
        var src = Src("RentNearBy.Api", "Handlers", "StayJobHandlers.cs");
        foreach (var name in new[] { "Create(", "Update(", "Close(" })
        {
            var body = src[src.IndexOf("public static async Task<IResult> " + name, StringComparison.Ordinal)..];
            body = body[..body.IndexOf("private static async Task<IResult>", StringComparison.Ordinal)];
            Assert.Contains("IRateLimitService limiter", body);
            Assert.Contains("OwnerHttp.WriteLimitAsync", body);
        }
        Assert.DoesNotContain("PublishAsync(SeekerQueue", src);
        Assert.DoesNotContain("StayJobPostedPublisher.PublishAsync(", src);
        Assert.Contains("StageSeekerBroadcastAsync(db, stay.Id, job.Id", src);
    }

    [Fact]
    public void Seeker_broadcast_key_is_one_per_stay_per_ist_day()
    {
        var stay = Guid.NewGuid();
        var day = new DateOnly(2026, 11, 22);
        Assert.Equal(StayJobPostedPublisher.SeekerKey(stay, day), StayJobPostedPublisher.SeekerKey(stay, day));
        Assert.NotEqual(StayJobPostedPublisher.SeekerKey(stay, day), StayJobPostedPublisher.SeekerKey(stay, day.AddDays(1)));
        Assert.Contains("ON CONFLICT (\"Key\") DO NOTHING", StayJobPostedPublisher.StageSql);
        Assert.True(StayJobPostedPublisher.SeekerKey(stay, day).Length <= 120);
    }

    [Fact]
    public async Task Thirty_first_write_in_a_minute_is_429_with_retry_after()
    {
        var limiter = new InMemoryRateLimitService();
        var user = Guid.NewGuid();
        IResult last = OkResponse(new { });
        var http = new DefaultHttpContext();
        for (var i = 0; i < 31; i++)
            last = await OwnerHttp.WriteLimitAsync(limiter, http, OwnerRateLimitKeys.Write(user), () => Task.FromResult<IResult>(OkResponse(new { })));
        Assert.Equal(429, Status(last));
        Assert.True(http.Response.Headers.ContainsKey("Retry-After"));
    }

    [Fact]
    public async Task Refused_writes_give_the_write_permit_back_but_still_hit_the_attempt_bucket()
    {
        var limiter = new InMemoryRateLimitService();
        var user = Guid.NewGuid();
        IResult last = OkResponse(new { });
        for (var i = 0; i < OwnerRateLimitKeys.AttemptsPerMinute + 1; i++)
            last = await OwnerHttp.WriteLimitAsync(limiter, new DefaultHttpContext(), OwnerRateLimitKeys.Write(user),
                () => Task.FromResult<IResult>(ConflictResponse("No.", "X")));
        Assert.Equal(429, Status(last));
    }

    [Fact]
    public async Task A_refused_write_does_not_use_the_write_bucket()
    {
        var limiter = new InMemoryRateLimitService();
        var user = Guid.NewGuid();
        for (var i = 0; i < 40; i++)
            await OwnerHttp.WriteLimitAsync(limiter, new DefaultHttpContext(), OwnerRateLimitKeys.Write(user),
                () => Task.FromResult<IResult>(ConflictResponse("No.", "X")));
        var ok = await OwnerHttp.WriteLimitAsync(limiter, new DefaultHttpContext(), OwnerRateLimitKeys.Write(user),
            () => Task.FromResult<IResult>(OkResponse(new { })));
        Assert.Equal(200, Status(ok));
    }
}
