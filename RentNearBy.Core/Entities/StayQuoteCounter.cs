namespace RentNearBy.Core.Entities;

// One row per owner: the monthly download count (IST yyyymm) and the Q-/PI- sequences. Also the reserve lock row.
public class StayQuoteCounter
{
    public Guid UserId { get; set; }
    public int MonthKey { get; set; }
    public short Used { get; set; }
    public int NextQuoteNo { get; set; } = 1;
    public int NextProformaNo { get; set; } = 1;
    public DateTime UpdatedAt { get; set; }
}
