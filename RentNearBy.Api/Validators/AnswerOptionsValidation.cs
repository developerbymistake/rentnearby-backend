using System.Text.Json;

namespace RentNearBy.Api.Validators;

// Shared by CreateQuestionTemplateRequestValidator and UpdateQuestionTemplateRequestValidator —
// both need the identical structural check, and this is correctness-critical parsing logic
// that must stay in sync between the two rather than be duplicated. Before this existed,
// only NotEmpty was checked server-side: a malformed shape, a missing key/text on one
// option, or a duplicate key within the same template could be persisted, and the consumer
// app's single try/catch around the whole options list meant one bad entry silently zeroed
// out ALL answer options for that question.
public static class AnswerOptionsValidation
{
    public static bool IsValid(string? json)
    {
        if (json == null) return true; // null is handled by the callers' own NotEmpty/When rules
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return false;
            if (doc.RootElement.GetArrayLength() == 0) return false;

            var seenKeys = new HashSet<string>();
            foreach (var option in doc.RootElement.EnumerateArray())
            {
                if (option.ValueKind != JsonValueKind.Object) return false;

                if (!option.TryGetProperty("key", out var keyEl) ||
                    keyEl.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(keyEl.GetString()))
                    return false;

                if (!option.TryGetProperty("text", out var textEl) ||
                    textEl.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(textEl.GetString()))
                    return false;

                if (!seenKeys.Add(keyEl.GetString()!)) return false; // duplicate key within this template
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
