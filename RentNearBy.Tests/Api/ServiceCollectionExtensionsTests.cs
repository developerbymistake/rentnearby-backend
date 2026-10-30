using RentNearBy.Api.Extensions;

namespace RentNearBy.Tests.Api;

public class ServiceCollectionExtensionsTests
{
    [Theory]
    [InlineData("Cancel", PlayServerActions.Cancel)]
    [InlineData("cancel", PlayServerActions.Cancel)]
    [InlineData(" CANCEL ", PlayServerActions.Cancel)]
    [InlineData("RevokeProrated", PlayServerActions.RevokeProrated)]
    [InlineData(" RevokeProrated ", PlayServerActions.RevokeProrated)]
    [InlineData("revokeprorated", PlayServerActions.RevokeProrated)]
    [InlineData("RevokeFull", null)]
    [InlineData("Refund", null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void NormalizeRoleChangeAction_OnlyCancelOrRevokeProrated(string? value, string? expected)
        => Assert.Equal(expected, ServiceCollectionExtensions.NormalizeRoleChangeAction(value));
}
