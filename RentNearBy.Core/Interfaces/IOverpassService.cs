namespace RentNearBy.Core.Interfaces;

public interface IOverpassService
{
    /// <summary>
    /// Fetches cities/towns within a district via Overpass API.
    /// Returns empty list on any failure — caller must handle gracefully.
    /// </summary>
    Task<List<(string Name, double Lat, double Lng)>> FetchCitiesAsync(string districtName, string stateName);
}
