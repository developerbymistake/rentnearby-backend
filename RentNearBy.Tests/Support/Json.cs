using System.Text.Json;

namespace RentNearBy.Tests.Support;

// Google-style JSON from anonymous objects: property names are written exactly as declared.
public static class Json
{
    public static readonly object Empty = new { };

    public static string Of(object value) => JsonSerializer.Serialize(value);
}
