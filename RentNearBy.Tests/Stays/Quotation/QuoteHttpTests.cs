using System.Security.Claims;
using System.Text.Json;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using RentNearBy.Api.Handlers.Owner;
using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.Owner.Quote;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Core.Models.Owner.Quote;
using RentNearBy.Infrastructure.Services.Owner.Quote;

namespace RentNearBy.Tests.Stays.Quotation;

// BUILD_PLAN 4.2: the wire shape of every refusal ({ type, message, data } with the contract status) and the handler front door
// (401 without a caller, 400 ValidationError with the field map before any service call, plain 404 for an unknown image kind).
public class QuoteHttpTests
{
    private static async Task<(int Status, JsonElement Body)> RunAsync(IResult result)
    {
        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        http.Response.Body = new MemoryStream();
        await result.ExecuteAsync(http);
        http.Response.Body.Position = 0;
        var text = await new StreamReader(http.Response.Body).ReadToEndAsync();
        return (http.Response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    [Fact]
    public async Task Month_limit_is_409_with_the_cap_data()
    {
        var resetsAt = new DateTime(2026, 11, 30, 18, 30, 0, DateTimeKind.Utc);
        var result = QuoteResult<QuoteReserveDto>.Fail(QuoteErrors.MonthLimit,
            new QuoteLimitDataDto(PlanFeatureKeys.StayQuotationsPerMonth, 2, 2, true, resetsAt), QuoteLimitMessages.For(2, true));
        var (status, body) = await RunAsync(QuoteHttp.Refuse(result));
        Assert.Equal(409, status);
        Assert.Equal("QuotationMonthLimit", body.GetProperty("error").GetProperty("type").GetString());
        var data = body.GetProperty("data");
        Assert.Equal("StayQuotationsPerMonth", data.GetProperty("key").GetString());
        Assert.Equal((2, 2, true), (data.GetProperty("limit").GetInt32(), data.GetProperty("used").GetInt32(), data.GetProperty("canUpgrade").GetBoolean()));
        Assert.Equal(resetsAt, data.GetProperty("resetsAt").GetDateTime().ToUniversalTime());
    }

    [Theory]
    [InlineData(QuoteErrors.StayNotApproved, 409)]
    [InlineData(QuoteErrors.TermsVersionUnknown, 400)]
    [InlineData(QuoteErrors.GstinMismatch, 400)]
    [InlineData(QuoteErrors.BankUnavailable, 503)]
    [InlineData(QuoteErrors.ImageMissing, 404)]
    [InlineData(QuoteErrors.StayNotFound, 404)]
    public async Task Typed_refusals_carry_their_status_and_type(string type, int expected)
    {
        var (status, body) = await RunAsync(QuoteHttp.Refuse(QuoteResult<object>.Fail(type)));
        Assert.Equal(expected, status);
        Assert.Equal(type, body.GetProperty("error").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Account_gone_mid_request_is_401_and_stale_edit_is_the_owner_409()
    {
        Assert.Equal(401, (await RunAsync(QuoteHttp.Refuse(QuoteResult<object>.Fail(StayQuoteProfileService.UnauthorizedType)))).Status);
        var (status, body) = await RunAsync(QuoteHttp.Refuse(QuoteResult<object>.Fail(OwnerErrorTypes.StaleEdit)));
        Assert.Equal(409, status);
        Assert.Equal(OwnerErrorTypes.StaleEdit, body.GetProperty("error").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Validation_error_is_400_with_a_camel_case_field_map_and_static_messages()
    {
        var request = new QuoteProfileRequest { Phone = "12345", BankMode = QuoteBankModes.Set, Bank = new QuoteBankRequest { AccountNo = "77", Ifsc = "X" } };
        var validation = await new QuoteProfileRequestValidator().ValidateAsync(request);
        var (status, body) = await RunAsync(QuoteHttp.Invalid(validation));
        Assert.Equal(400, status);
        Assert.Equal("ValidationError", body.GetProperty("error").GetProperty("type").GetString());
        var data = body.GetProperty("data");
        Assert.Equal(QuoteProfileRequestValidator.PhoneBad, data.GetProperty("phone").GetString());
        Assert.Equal(QuoteProfileRequestValidator.AccountBad, data.GetProperty("bank.accountNo").GetString());
        Assert.DoesNotContain("12345", body.GetRawText());
    }

    private sealed class NeverValidator<T> : AbstractValidator<T>;

    [Fact]
    public async Task No_caller_is_401_before_validation_or_service()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        var result = await StayQuotationHandlers.Reserve(new QuoteReserveRequest(), new NeverValidator<QuoteReserveRequest>(), anonymous, null!,
            new DefaultHttpContext(), default);
        Assert.Equal(401, (await RunAsync(result)).Status);
    }

    [Fact]
    public async Task Invalid_reserve_body_never_reaches_the_service()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "test"));
        var result = await StayQuotationHandlers.Reserve(new QuoteReserveRequest { Kind = "Invoice" }, new QuoteReserveRequestValidator(), user, null!,
            new DefaultHttpContext(), default);
        Assert.Equal(400, (await RunAsync(result)).Status);
    }

    private sealed class Allow : IRateLimitService
    {
        public Task<RateLimitResult> CheckAsync(string key, int maxAttempts, TimeSpan window, int permits = 1) => Task.FromResult(new RateLimitResult(true, maxAttempts, null));
        public Task ReleaseAsync(string key, string token) => Task.CompletedTask;
    }

    [Fact]
    public async Task Unknown_image_kind_is_a_plain_404()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "test"));
        var result = await StayQuotationHandlers.GetImage("stamp", user, null!, new Allow(), new DefaultHttpContext(), default);
        Assert.Equal(404, (await RunAsync(result)).Status);
    }
}
