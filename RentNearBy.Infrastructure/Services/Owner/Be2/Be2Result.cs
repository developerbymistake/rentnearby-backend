using RentNearBy.Core.Models.Owner;

namespace RentNearBy.Infrastructure.Services.Owner.Be2;

// Answer of a lane BE2 service: a value, or an error type (appendix A) with its message and data. Replay = idempotent repeat
// (no write happened); Created = a new row was written (201 for admin creates). DistrictId is busted by the handler after commit.
public sealed record Be2Result<T>(
    T? Value, string? ErrorType = null, string? Message = null, object? Data = null, bool Replay = false, bool Created = false,
    Guid? DistrictId = null, bool Changed = false)
{
    public const string NotFound = "NotFound";

    public bool Ok => ErrorType is null;

    public static Be2Result<T> Fail(string type, object? data = null) => new(default, type, OwnerErrorTypes.MessageOf(type), data);

    public static Be2Result<T> Fail(string type, string message, object? data = null) => new(default, type, message, data);

    public static Be2Result<T> Missing(string message) => new(default, NotFound, message);
}
