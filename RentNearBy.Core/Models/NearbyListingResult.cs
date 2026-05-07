using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Models;

public record NearbyListingResult(Listing Listing, double DistanceKm);
