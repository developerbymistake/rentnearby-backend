namespace RentNearBy.Infrastructure.Services;

public sealed class InvalidPhotoException : Exception
{
    public const string UserMessage = "File must be a valid image (JPEG, PNG or WebP)";

    public InvalidPhotoException() : base(UserMessage) { }

    public InvalidPhotoException(Exception inner) : base(UserMessage, inner) { }
}
