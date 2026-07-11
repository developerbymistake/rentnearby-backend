namespace RentNearBy.Core.DTOs.Requests;

public class CreateQuestionTemplateRequest
{
    public string Key { get; set; } = string.Empty;
    public string ListingType { get; set; } = "Both";
    public Guid? RoomTypeId { get; set; }
    public Guid? PlotTypeId { get; set; }
    public string QuestionText { get; set; } = string.Empty;
    public string AnswerOptionsJson { get; set; } = "[]";
    public int SortOrder { get; set; } = 999;
}
