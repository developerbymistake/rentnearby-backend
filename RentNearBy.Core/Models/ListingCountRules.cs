using System.Linq.Expressions;
using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Models;

// The one count rule for rooms and plots: a soft-deleted row is never counted, and "live" is a non-deleted
// active row. Admin stats, the user list and the user detail all use these, so their numbers agree.
public static class ListingCountRules
{
    public static readonly Expression<Func<RoomListing, bool>> RoomExisting = l => !l.IsDeleted;
    public static readonly Expression<Func<RoomListing, bool>> RoomLive = l => !l.IsDeleted && l.IsActive;
    public static readonly Expression<Func<PlotListing, bool>> PlotExisting = l => !l.IsDeleted;
    public static readonly Expression<Func<PlotListing, bool>> PlotLive = l => !l.IsDeleted && l.IsActive;
}
