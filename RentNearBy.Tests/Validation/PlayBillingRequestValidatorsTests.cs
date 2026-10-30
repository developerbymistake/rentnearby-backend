using FluentValidation.TestHelper;
using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Tests.Validation;

public class PlayBillingRequestValidatorsTests
{
    private static readonly VerifyPlayPurchaseRequestValidator Verify = new();
    private static readonly RestorePlayPurchasesRequestValidator Restore = new();

    [Theory]
    [InlineData(19, false)]
    [InlineData(20, true)]
    [InlineData(1024, true)]
    [InlineData(1025, false)]
    public void Verify_TokenLength(int length, bool valid)
    {
        var result = Verify.TestValidate(new VerifyPlayPurchaseRequest(Requests.Token(length), "room_plus"));

        if (valid) result.ShouldNotHaveAnyValidationErrors();
        else result.ShouldHaveValidationErrorFor(x => x.PurchaseToken);
    }

    [Theory]
    [InlineData("room_plus", true)]
    [InlineData("room.plus", true)]
    [InlineData("1room", true)]
    [InlineData("Room_Plus", false)]
    [InlineData("_x", false)]
    [InlineData(".x", false)]
    [InlineData("room-plus", false)]
    [InlineData("", false)]
    public void Verify_ProductId(string productId, bool valid)
    {
        var result = Verify.TestValidate(new VerifyPlayPurchaseRequest(Requests.Token(40), productId));

        if (valid) result.ShouldNotHaveAnyValidationErrors();
        else result.ShouldHaveValidationErrorFor(x => x.ProductId);
    }

    [Theory]
    [InlineData(40, true)]
    [InlineData(41, false)]
    public void Verify_ProductIdLength(int length, bool valid)
        => Assert.Equal(valid, Verify.TestValidate(new VerifyPlayPurchaseRequest(Requests.Token(40), new string('a', length))).IsValid);

    private static RestorePlayPurchasesRequest RestoreOf(int count)
        => new(Enumerable.Range(0, count).Select(_ => new RestorePlayItem(Requests.Token(40), "room_plus")).ToList());

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(10, true)]
    [InlineData(11, false)]
    public void Restore_ItemCount(int count, bool valid)
    {
        var result = Restore.TestValidate(RestoreOf(count));

        if (valid) result.ShouldNotHaveAnyValidationErrors();
        else ValidationAssert.HasMessage(result, "Purchases must contain 1 to 10 items");
    }

    [Fact]
    public void Restore_NullList_Error()
        => Assert.False(Restore.TestValidate(new RestorePlayPurchasesRequest(null!)).IsValid);

    [Fact]
    public void Restore_BadItem_ChildRulesApplyPerItem()
    {
        var request = new RestorePlayPurchasesRequest(
        [
            new RestorePlayItem(Requests.Token(40), "room_plus"),
            new RestorePlayItem(Requests.Token(5), "Room_Plus"),
        ]);

        var result = Restore.TestValidate(request);

        Assert.Equal(2, result.Errors.Count);
        Assert.All(result.Errors, e => Assert.StartsWith("Purchases[1]", e.PropertyName));
        ValidationAssert.HasMessage(result, "ProductId is not a valid Play product id");
    }
}
