namespace RentNearBy.Core.DTOs.Responses;

// Places of one step: counted asks, quiet asks, Active shortlist rows and what is still free.
public class TripStepSlotsDto
{
    public string StepKey { get; set; } = string.Empty;
    public int Counted { get; set; }
    public int Quiet { get; set; }
    public int Shortlisted { get; set; }
    public int Free { get; set; }
    public int MaxCounted { get; set; }
    public int MaxTotal { get; set; }
    public DateTime? NextQuietAt { get; set; }
}

// data of a 409 StepFull.
public class StepFullDto
{
    public string Case { get; set; } = string.Empty;
    public int Counted { get; set; }
    public int Quiet { get; set; }
    public int Shortlisted { get; set; }
    public int Free { get; set; }
    public int MaxCounted { get; set; }
    public int MaxTotal { get; set; }
    public DateTime? NextQuietAt { get; set; }
    public List<StepFullAskDto> QuietAsks { get; set; } = new();
    public List<StepFullAskDto> WaitingAsks { get; set; } = new();
}

public class StepFullAskDto
{
    public Guid EnquiryId { get; set; }
    public Guid ListingId { get; set; }
    public string ListingName { get; set; } = string.Empty;
    public DateTime AskedAt { get; set; }
}
