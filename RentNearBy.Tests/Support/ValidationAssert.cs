using FluentValidation.Results;

namespace RentNearBy.Tests.Support;

// Matching by message/prefix keeps tests independent of how FluentValidation spells nested property paths.
public static class ValidationAssert
{
    public static void HasMessage(ValidationResult result, string message)
        => Assert.Contains(result.Errors, e => e.ErrorMessage == message);

    public static void HasErrorOn(ValidationResult result, string propertyPrefix)
        => Assert.Contains(result.Errors, e => e.PropertyName.StartsWith(propertyPrefix, StringComparison.Ordinal));

    public static void Valid(ValidationResult result)
        => Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => $"{e.PropertyName}: {e.ErrorMessage}")));
}
