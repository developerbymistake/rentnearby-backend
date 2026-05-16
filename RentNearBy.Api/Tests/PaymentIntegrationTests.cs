using FluentAssertions;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.Entities;
using RentNearBy.Infrastructure.Services;
using Xunit;

namespace RentNearBy.Api.Tests;

public class PaymentIntegrationTests
{
    private readonly IPaymentService _paymentService;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IRazorpayService _razorpayService;

    public PaymentIntegrationTests(IPaymentService paymentService, IUnitOfWork unitOfWork, IRazorpayService razorpayService)
    {
        _paymentService = paymentService;
        _unitOfWork = unitOfWork;
        _razorpayService = razorpayService;
    }

    #region FREE Plan Tests

    [Fact]
    public async Task ActivateFreePlan_FirstTime_Should_CreateMembershipAndActivateListing()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var listingId = Guid.NewGuid();
        var user = new User { Id = userId, PhoneNumber = "9999999999", HasUsedFreePlan = false };
        var listing = new Listing
        {
            Id = listingId,
            UserId = userId,
            IsActive = false,
            RoomTypeId = Guid.NewGuid(),
            DistrictId = Guid.NewGuid(),
            PriceMonthly = 5000,
            Latitude = 19.0760m,
            Longitude = 72.8777m
        };

        await _unitOfWork.Users.AddAsync(user);
        await _unitOfWork.Listings.AddAsync(listing);
        await _unitOfWork.SaveChangesAsync();

        // Act
        var response = await _paymentService.InitiatePaymentAsync(userId, listingId, "FREE");
        var verifyRequest = new VerifyPaymentRequest
        {
            RazorpayOrderId = response.RazorpayOrderId ?? string.Empty,
            RazorpayPaymentId = "pay_test_000",
            RazorpaySignature = "verified"
        };
        var verifyResponse = await _paymentService.VerifyAndActivateAsync(userId, verifyRequest);

        // Assert
        response.Amount.Should().Be(0);
        response.PlanType.Should().Be("FREE");
        verifyResponse.Success.Should().BeTrue();
        verifyResponse.PlanType.Should().Be("FREE");
        verifyResponse.MaxRooms.Should().Be(1);

        var updatedUser = await _unitOfWork.Users.GetByIdAsync(userId);
        updatedUser.HasUsedFreePlan.Should().BeTrue();

        var membership = await _unitOfWork.UserMemberships.GetActiveByUserIdAsync(userId);
        membership.Should().NotBeNull();
        membership.PlanType.Should().Be("FREE");
        membership.MaxRooms.Should().Be(1);

        var updatedListing = await _unitOfWork.Listings.GetByIdAsync(listingId);
        updatedListing.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task ActivateFreePlan_SecondTime_Should_Fail()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var listingId = Guid.NewGuid();
        var user = new User { Id = userId, PhoneNumber = "9999999999", HasUsedFreePlan = true };
        var listing = new Listing
        {
            Id = listingId,
            UserId = userId,
            IsActive = false,
            RoomTypeId = Guid.NewGuid(),
            DistrictId = Guid.NewGuid(),
            PriceMonthly = 5000,
            Latitude = 19.0760m,
            Longitude = 72.8777m
        };

        await _unitOfWork.Users.AddAsync(user);
        await _unitOfWork.Listings.AddAsync(listing);
        await _unitOfWork.SaveChangesAsync();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _paymentService.InitiatePaymentAsync(userId, listingId, "FREE")
        );
    }

    #endregion

    #region PAID Plan Tests

    [Fact]
    public async Task ActivatePaidPlan_Should_CreateOrderAndMembership()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var listingId = Guid.NewGuid();
        var user = new User { Id = userId, PhoneNumber = "9999999999", HasUsedFreePlan = false };
        var listing = new Listing
        {
            Id = listingId,
            UserId = userId,
            IsActive = false,
            RoomTypeId = Guid.NewGuid(),
            DistrictId = Guid.NewGuid(),
            PriceMonthly = 8000,
            Latitude = 19.0760m,
            Longitude = 72.8777m
        };

        await _unitOfWork.Users.AddAsync(user);
        await _unitOfWork.Listings.AddAsync(listing);
        await _unitOfWork.SaveChangesAsync();

        // Act
        var response = await _paymentService.InitiatePaymentAsync(userId, listingId, "PAID");

        // Assert
        response.Amount.Should().Be(99);
        response.PlanType.Should().Be("PAID");
        response.RazorpayOrderId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task VerifyPaidPayment_Should_ActivateListingAndCreate30DayMembership()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var listingId = Guid.NewGuid();
        var user = new User { Id = userId, PhoneNumber = "9999999999", HasUsedFreePlan = false };
        var listing = new Listing
        {
            Id = listingId,
            UserId = userId,
            IsActive = false,
            RoomTypeId = Guid.NewGuid(),
            DistrictId = Guid.NewGuid(),
            PriceMonthly = 8000,
            Latitude = 19.0760m,
            Longitude = 72.8777m
        };

        await _unitOfWork.Users.AddAsync(user);
        await _unitOfWork.Listings.AddAsync(listing);
        await _unitOfWork.SaveChangesAsync();

        var initiateResponse = await _paymentService.InitiatePaymentAsync(userId, listingId, "PAID");

        // Act
        var verifyRequest = new VerifyPaymentRequest
        {
            RazorpayOrderId = initiateResponse.RazorpayOrderId,
            RazorpayPaymentId = "pay_test_123456",
            RazorpaySignature = GenerateValidSignature(initiateResponse.RazorpayOrderId, "pay_test_123456")
        };
        var verifyResponse = await _paymentService.VerifyAndActivateAsync(userId, verifyRequest);

        // Assert
        verifyResponse.Success.Should().BeTrue();
        verifyResponse.PlanType.Should().Be("PAID");
        verifyResponse.MaxRooms.Should().Be(2);

        var membership = await _unitOfWork.UserMemberships.GetActiveByUserIdAsync(userId);
        membership.ValidUntil.Should().BeGreaterThan(DateTime.UtcNow.AddDays(25));
        membership.MaxRooms.Should().Be(2);

        var updatedListing = await _unitOfWork.Listings.GetByIdAsync(listingId);
        updatedListing.IsActive.Should().BeTrue();
    }

    #endregion

    #region Room Limit Tests

    [Fact]
    public async Task CanActivateListing_WithFreePlan_And_AlreadyOneRoom_Should_ReturnFalse()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = new User { Id = userId, PhoneNumber = "9999999999" };
        var membership = new UserMembership
        {
            UserId = userId,
            PlanType = "FREE",
            MaxRooms = 1,
            ValidFrom = DateTime.UtcNow,
            ValidUntil = DateTime.UtcNow.AddDays(10),
            IsActive = true
        };
        var existingListing = new Listing
        {
            UserId = userId,
            IsActive = true,
            RoomTypeId = Guid.NewGuid(),
            DistrictId = Guid.NewGuid(),
            PriceMonthly = 5000,
            Latitude = 19.0760m,
            Longitude = 72.8777m
        };

        await _unitOfWork.Users.AddAsync(user);
        await _unitOfWork.UserMemberships.AddAsync(membership);
        await _unitOfWork.Listings.AddAsync(existingListing);
        await _unitOfWork.SaveChangesAsync();

        // Act
        var canActivate = await _paymentService.CanUserActivateListingAsync(userId);

        // Assert
        canActivate.Should().BeFalse();
    }

    [Fact]
    public async Task CanActivateListing_WithPaidPlan_And_TwoRooms_Should_ReturnFalse()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = new User { Id = userId, PhoneNumber = "9999999999" };
        var membership = new UserMembership
        {
            UserId = userId,
            PlanType = "PAID",
            MaxRooms = 2,
            ValidFrom = DateTime.UtcNow,
            ValidUntil = DateTime.UtcNow.AddDays(30),
            IsActive = true
        };
        var listing1 = new Listing
        {
            UserId = userId,
            IsActive = true,
            RoomTypeId = Guid.NewGuid(),
            DistrictId = Guid.NewGuid(),
            PriceMonthly = 5000,
            Latitude = 19.0760m,
            Longitude = 72.8777m
        };
        var listing2 = new Listing
        {
            UserId = userId,
            IsActive = true,
            RoomTypeId = Guid.NewGuid(),
            DistrictId = Guid.NewGuid(),
            PriceMonthly = 8000,
            Latitude = 19.0760m,
            Longitude = 72.8777m
        };

        await _unitOfWork.Users.AddAsync(user);
        await _unitOfWork.UserMemberships.AddAsync(membership);
        await _unitOfWork.Listings.AddAsync(listing1);
        await _unitOfWork.Listings.AddAsync(listing2);
        await _unitOfWork.SaveChangesAsync();

        // Act
        var canActivate = await _paymentService.CanUserActivateListingAsync(userId);

        // Assert
        canActivate.Should().BeFalse();
    }

    #endregion

    #region Error Handling Tests

    [Fact]
    public async Task InitiatePayment_WithInvalidPlanType_Should_ThrowArgumentException()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var listingId = Guid.NewGuid();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => _paymentService.InitiatePaymentAsync(userId, listingId, "INVALID")
        );
    }

    [Fact]
    public async Task VerifyPayment_WithWrongOwner_Should_ThrowUnauthorizedAccessException()
    {
        // Arrange
        var userId1 = Guid.NewGuid();
        var userId2 = Guid.NewGuid();
        var listingId = Guid.NewGuid();

        var user1 = new User { Id = userId1, PhoneNumber = "1111111111" };
        var listing = new Listing
        {
            Id = listingId,
            UserId = userId1,
            IsActive = false,
            RoomTypeId = Guid.NewGuid(),
            DistrictId = Guid.NewGuid(),
            PriceMonthly = 5000,
            Latitude = 19.0760m,
            Longitude = 72.8777m
        };

        await _unitOfWork.Users.AddAsync(user1);
        await _unitOfWork.Listings.AddAsync(listing);
        await _unitOfWork.SaveChangesAsync();

        var response = await _paymentService.InitiatePaymentAsync(userId1, listingId, "FREE");

        var verifyRequest = new VerifyPaymentRequest
        {
            RazorpayOrderId = response.RazorpayOrderId ?? string.Empty,
            RazorpayPaymentId = "pay_test",
            RazorpaySignature = "sig"
        };

        // Act & Assert
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _paymentService.VerifyAndActivateAsync(userId2, verifyRequest)
        );
    }

    #endregion

    #region Helper Methods

    private string GenerateValidSignature(string orderId, string paymentId)
    {
        return _razorpayService.VerifyPaymentSignature(orderId, paymentId, "test")
            ? "valid_signature_hash"
            : "invalid";
    }

    #endregion
}
