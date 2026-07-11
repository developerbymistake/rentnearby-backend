namespace RentNearBy.Core.DTOs.Requests;

public class UpdateQuestionTemplateRequest
{
    public string? QuestionText { get; set; }
    public string? AnswerOptionsJson { get; set; }
    public int? SortOrder { get; set; }
}
