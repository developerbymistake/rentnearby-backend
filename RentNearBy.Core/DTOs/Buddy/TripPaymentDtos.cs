namespace RentNearBy.Core.DTOs.Buddy;

// E9
public class SaidPaidRequestDto
{
    public Guid OpId { get; set; }
    public Guid ToUserId { get; set; }
    public int Amount { get; set; }
}

// E12
public class RemindRequestDto
{
    public Guid FromUserId { get; set; }
}

// E9, E10, E18, E19. Payment is one element of E8 payments (null when nothing is open between the two any more).
public class PaymentWriteResponseDto
{
    public PaymentRowDto? Payment { get; set; }
    public long BalancesVersion { get; set; }
}
