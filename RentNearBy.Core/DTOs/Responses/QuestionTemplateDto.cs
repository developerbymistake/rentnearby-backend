namespace RentNearBy.Core.DTOs.Responses;

public class QuestionTemplateDto
{
    public Guid Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string ListingType { get; set; } = "Stay";
    public Guid? StayTypeId { get; set; }
    public string QuestionText { get; set; } = string.Empty;
    public string AnswerOptionsJson { get; set; } = "[]";
    public int SortOrder { get; set; }
    public bool IsActive { get; set; }
}
