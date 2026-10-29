namespace RentNearBy.Core.Entities;

// One taxi per driver while not deleted (ix_taxilistings_user_live). Step fields stay nullable while
// the taxi is a Draft; TaxiSetupRules derives the step flags from them.
public class TaxiListing
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }

    public Guid? VehicleTypeId { get; set; }
    public short? Seats { get; set; }   // 12 = "12+"
    public string? NumberPlate { get; set; }   // normalised, see TaxiPlate
    public string? PlatePhotoUrl { get; set; }
    public string? PlatePhotoPath { get; set; }
    public string? ContactPhone { get; set; }
    public string? WhatsAppNumber { get; set; }

    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public Guid? DistrictId { get; set; }
    public Guid? CityId { get; set; }

    public string Status { get; set; } = "Draft";
    // Set on admin approval, never cleared: keeps the role locked after a later reject or delete.
    public bool EverApproved { get; set; }
    public bool IsAvailable { get; set; } = true;
    public string? RejectedReason { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public Guid? ReviewedByAdminId { get; set; }

    // Cheapest route fare, rewritten on every route/fare write.
    public int? MinFare { get; set; }
    public string? Slug { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsDeleted { get; set; } = false;
    public DateTime? DeletedAt { get; set; }

    public User User { get; set; } = null!;
    public TaxiVehicleType? VehicleType { get; set; }
    public District? District { get; set; }
    public City? City { get; set; }
    public ICollection<TaxiRoute> Routes { get; set; } = new List<TaxiRoute>();
}
