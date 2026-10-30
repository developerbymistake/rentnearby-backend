using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.Support;

internal sealed class ActiveRowBuilder
{
    private Guid _id = TestIds.UserPlan1;
    private Guid _userId = TestIds.User1;
    private Guid _planId = TestIds.RoomPlus;
    private string _source = UserPlanSources.Subscription;
    private DateTime _startsAt = Dates.Past;
    private DateTime _endsAt = Dates.Far;

    public ActiveRowBuilder WithId(Guid id) { _id = id; return this; }
    public ActiveRowBuilder ForUser(Guid userId) { _userId = userId; return this; }
    public ActiveRowBuilder OnPlan(Guid planId) { _planId = planId; return this; }
    public ActiveRowBuilder WithSource(string source) { _source = source; return this; }
    public ActiveRowBuilder Between(DateTime startsAt, DateTime endsAt) { _startsAt = startsAt; _endsAt = endsAt; return this; }

    public PlanEntitlementService.ActiveRow Build() => new(_id, _userId, _planId, _source, _startsAt, _endsAt);
}
