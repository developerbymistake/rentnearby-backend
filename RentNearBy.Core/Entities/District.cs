using NetTopologySuite.Geometries;

namespace RentNearBy.Core.Entities;

public class District
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string StateName { get; set; } = string.Empty;
    public bool IsActive { get; set; } = false;
    public DateTime CreatedAt { get; set; }
    public Geometry? Boundary { get; set; }

    public ICollection<City> Cities { get; set; } = new List<City>();
    public ICollection<RoomListing> RoomListings { get; set; } = new List<RoomListing>();
}
