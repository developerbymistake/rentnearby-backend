namespace RentNearBy.Core.Entities;

public class QuestionTemplate
{
    public Guid Id { get; set; }
    public string Key { get; set; } = string.Empty; // stable, e.g. "is_available" — referenced from Message.PayloadJson
    public string ListingType { get; set; } = "Stay"; // "Stay"

    // Optional stay type scope; null means every stay.
    public Guid? StayTypeId { get; set; }

    public string QuestionText { get; set; } = string.Empty;

    // [{"key":"yes_available","text":"Yes, still available","sentiment":"positive"}, ...]
    public string AnswerOptionsJson { get; set; } = "[]";

    public int SortOrder { get; set; } = 999;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
}
