namespace RentNearBy.Core.Entities.Social;

// Support corrections of gender / birth month and year, written in the same transaction as the update.
public class ProfileIdentityAudit
{
    public long Id { get; set; }
    public Guid UserId { get; set; }
    public string OldGender { get; set; } = string.Empty;
    public string NewGender { get; set; } = string.Empty;
    public short OldBirthMonth { get; set; }
    public short NewBirthMonth { get; set; }
    public short OldBirthYear { get; set; }
    public short NewBirthYear { get; set; }
    public Guid AdminId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}
