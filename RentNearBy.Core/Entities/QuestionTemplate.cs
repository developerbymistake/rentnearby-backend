namespace RentNearBy.Core.Entities;

public class QuestionTemplate
{
    public Guid Id { get; set; }
    public string Key { get; set; } = string.Empty; // stable, e.g. "is_available" — referenced from Message.PayloadJson
    public string ListingType { get; set; } = "Both"; // "Room" | "Plot" | "Both"
    public string QuestionText { get; set; } = string.Empty;

    // [{"key":"yes_available","text":"Yes, still available","sentiment":"positive"}, ...]
    public string AnswerOptionsJson { get; set; } = "[]";

    public int SortOrder { get; set; } = 999;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
}
