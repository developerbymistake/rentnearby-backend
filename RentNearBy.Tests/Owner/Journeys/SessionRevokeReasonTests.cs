using RentNearBy.Api.Extensions;
using RentNearBy.Tests.Trips;

namespace RentNearBy.Tests.Owner.Journeys;

// FB-42 (D-34, JT-B-LC-4 / JT-B-LC-5): a session the server ended answers 401 with the reason.
public class SessionRevokeReasonTests
{
    private static string Src(params string[] parts) => File.ReadAllText(Path.Combine([CompletionCounterGuardTests.Root(), .. parts]));

    [Theory]
    [InlineData(SessionRevokeReasons.SessionRoleChanged, "Role updated. Please log in again.")]
    [InlineData(SessionRevokeReasons.AccountSuspended, "Your account is paused.")]
    public void Body_carries_the_reason_as_the_error_type(string reason, string message)
    {
        var body = AuthenticationExtensions.RevokedBody(reason);
        Assert.Equal(401, body.Code);
        Assert.Equal(reason, body.Error!.Type);
        Assert.Equal(message, body.Error.Message);
        Assert.True(reason.Length <= SessionRevokeReasons.MaxLength);
    }

    [Fact]
    public void Role_change_and_suspend_store_their_reason()
    {
        Assert.Contains("RevokeAllUserSessionsAsync(user.Id, SessionRevokeReasons.SessionRoleChanged)", Src("RentNearBy.Api", "Handlers", "AdminRoleHandlers.cs"));
        Assert.Contains("SetProperty(x => x.RevokeReason, SessionRevokeReasons.AccountSuspended)", Src("RentNearBy.Api", "Handlers", "AdminHandlers.cs"));
    }

    [Fact]
    public void The_reason_is_read_only_after_a_failed_check()
    {
        var src = Src("RentNearBy.Api", "Extensions", "AuthenticationExtensions.cs");
        Assert.True(src.IndexOf("if (session == null)", StringComparison.Ordinal) < src.IndexOf("GetRevokeReasonAsync", StringComparison.Ordinal));
        Assert.Contains("OnChallenge", src);
        Assert.False(SessionRevokeReasons.IsKnown(null));
        Assert.False(SessionRevokeReasons.IsKnown("Other"));
    }
}
