namespace RentNearBy.Core.DTOs.Responses;

public class ApiResponse<T>
{
    public T? Data { get; set; }
    public string Status { get; set; } = string.Empty;
    public int Code { get; set; }
    public long Timestamp { get; set; }
    public ApiError? Error { get; set; }
}

public class ApiError
{
    public string Message { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
}
