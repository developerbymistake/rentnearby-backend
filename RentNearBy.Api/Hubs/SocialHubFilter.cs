using Microsoft.AspNetCore.SignalR;
using RentNearBy.Api.Extensions;
using RentNearBy.Core.DTOs.Social;
using RentNearBy.Core.Models.Social;

namespace RentNearBy.Api.Hubs;

// Contract 3.1: every ChatHub method whose name starts with "Dm" is for seekers only. A non-seeker gets {ok:false, type:"SeekerOnly"}
// (DmRead returns without work). The live role is also re-checked inside every statement behind DmSend / DmRead.
public sealed class SocialHubFilter : IHubFilter
{
    public const string Prefix = "Dm";

    public static bool IsSocialMethod(string name) => name.StartsWith(Prefix, StringComparison.Ordinal);

    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocationContext, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        if (invocationContext.Hub is ChatHub && IsSocialMethod(invocationContext.HubMethodName)
            && !SeekerGate.IsSeeker(invocationContext.Context.User!))
        {
            return invocationContext.HubMethodName == nameof(ChatHub.DmSend)
                ? new DmSendResultDto { Ok = false, Type = SocialErrorTypes.SeekerOnly, Message = SocialErrorTypes.MessageOf(SocialErrorTypes.SeekerOnly) }
                : null;
        }
        return await next(invocationContext);
    }
}
