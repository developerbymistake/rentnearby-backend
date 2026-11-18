using FirebaseAdmin.Messaging;
using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.TaxiRequests;

public class FcmClassifyTests
{
    private const string BadTokenMessage = "The registration token is not a valid FCM registration token";

    [Theory]
    [InlineData(MessagingErrorCode.Unregistered)]
    [InlineData(MessagingErrorCode.SenderIdMismatch)]
    public void Permanent_token_errors_mark_invalid(MessagingErrorCode code)
    {
        Assert.Equal(FcmSendOutcome.InvalidToken, FcmService.Classify(false, code));
        Assert.True(FcmService.IsPermanentTokenError(code));
    }

    [Fact]
    public void Invalid_argument_naming_the_token_marks_invalid()
    {
        Assert.Equal(FcmSendOutcome.InvalidToken, FcmService.Classify(false, MessagingErrorCode.InvalidArgument, BadTokenMessage));
        Assert.True(FcmService.IsPermanentTokenError(MessagingErrorCode.InvalidArgument, "Invalid REGISTRATION TOKEN"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Android message is too big")]
    [InlineData("Invalid value at 'message.android.ttl'")]
    [InlineData("Invalid data payload key: from")]
    public void Invalid_argument_about_the_message_keeps_the_token(string? message)
    {
        Assert.Equal(FcmSendOutcome.Failed, FcmService.Classify(false, MessagingErrorCode.InvalidArgument, message));
        Assert.False(FcmService.IsPermanentTokenError(MessagingErrorCode.InvalidArgument, message));
    }

    [Theory]
    [InlineData(MessagingErrorCode.Internal)]
    [InlineData(MessagingErrorCode.Unavailable)]
    [InlineData(MessagingErrorCode.QuotaExceeded)]
    [InlineData(MessagingErrorCode.ThirdPartyAuthError)]
    public void Other_errors_stay_transient(MessagingErrorCode code)
    {
        Assert.Equal(FcmSendOutcome.Failed, FcmService.Classify(false, code));
        Assert.Equal(FcmSendOutcome.Failed, FcmService.Classify(false, code, BadTokenMessage));
    }

    [Fact]
    public void Success_is_sent_and_an_unknown_failure_is_transient()
    {
        Assert.Equal(FcmSendOutcome.Sent, FcmService.Classify(true, null));
        Assert.Equal(FcmSendOutcome.Failed, FcmService.Classify(false, null));
    }
}
