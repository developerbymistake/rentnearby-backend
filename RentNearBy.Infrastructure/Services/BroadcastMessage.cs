namespace RentNearBy.Infrastructure.Services;

public class BroadcastMessage
{
    public string Title  { get; set; } = string.Empty;
    public string Body   { get; set; } = string.Empty;
    public int    Offset { get; set; }
    public int    Limit  { get; set; } = 5000;
}
