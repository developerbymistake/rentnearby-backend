namespace RentNearBy.Infrastructure.Services.Owner.Quote;

// A quotation service answer: a value, or a typed refusal (QuoteErrors / OwnerErrorTypes) with optional data.
public sealed record QuoteResult<T>(T? Value, string? ErrorType = null, object? Data = null, string? Message = null)
{
    public bool Ok => ErrorType == null;

    public static QuoteResult<T> Success(T value) => new(value);

    public static QuoteResult<T> Fail(string type, object? data = null, string? message = null) => new(default, type, data, message);
}
