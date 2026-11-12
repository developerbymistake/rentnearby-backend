using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

// One projected query per network rebuild. Public filter is the literal TaxiHandlers.IsPublic predicate.
public class TaxiWaysNetworkRepository(ApplicationDbContext context) : ITaxiWaysNetworkSource
{
    private sealed record Row(
        Guid RouteId, Guid TaxiId, Guid UserId, string? DriverName, string? VehicleTypeName, string? VehicleCategory,
        short? Seats, string? NumberPlate, Guid? CityId, string? CityName, double? BaseLat, double? BaseLng,
        int? LocalDayRate, short PriorityRank, bool IsFeatured, int TripLeadsReceived, int TripLeadsAnsweredInApp,
        string FromName, double FromLat, double FromLng, string ToName, double ToLat, double ToLng,
        int Fare, string TripType, string? GeometryJson, double? DistanceKm, int? Minutes);

    public async Task<IReadOnlyList<TaxiWaysSourceRow>> LoadAsync(CancellationToken ct = default)
    {
        var rows = await context.Database
            .SqlQuery<Row>($"""
                SELECT r."Id"                     AS "RouteId",
                       t."Id"                     AS "TaxiId",
                       t."UserId",
                       u."Name"                   AS "DriverName",
                       v."Name"                   AS "VehicleTypeName",
                       v."Category"               AS "VehicleCategory",
                       t."Seats",
                       t."NumberPlate",
                       t."CityId",
                       c."Name"                   AS "CityName",
                       t."Latitude"::float8       AS "BaseLat",
                       t."Longitude"::float8      AS "BaseLng",
                       t."LocalDayRate",
                       t."PriorityRank",
                       t."IsFeatured",
                       t."TripLeadsReceived",
                       t."TripLeadsAnsweredInApp",
                       r."FromName",
                       r."FromLatitude"::float8   AS "FromLat",
                       r."FromLongitude"::float8  AS "FromLng",
                       r."ToName",
                       r."ToLatitude"::float8     AS "ToLat",
                       r."ToLongitude"::float8    AS "ToLng",
                       r."Fare",
                       r."TripType",
                       g."GeometryJson"::text     AS "GeometryJson",
                       g."DistanceKm"::float8     AS "DistanceKm",
                       g."Minutes"
                FROM "TaxiRoutes" r
                JOIN "TaxiListings" t ON t."Id" = r."TaxiListingId"
                  AND t."Status" = 'Approved' AND t."IsAvailable" = TRUE AND t."IsDeleted" = FALSE
                JOIN "Users" u ON u."Id" = t."UserId"
                LEFT JOIN "TaxiVehicleTypes" v ON v."Id" = t."VehicleTypeId"
                LEFT JOIN "Cities" c ON c."Id" = t."CityId"
                LEFT JOIN "TaxiRouteGeometries" g ON g."RouteId" = r."Id" AND g."Status" = 'Ok'
                ORDER BY r."Id"
                """)
            .ToListAsync(ct);

        return rows.Select(r => new TaxiWaysSourceRow
        {
            RouteId = r.RouteId,
            TaxiId = r.TaxiId,
            UserId = r.UserId,
            DriverName = r.DriverName,
            VehicleTypeName = r.VehicleTypeName,
            VehicleCategory = r.VehicleCategory,
            Seats = r.Seats,
            PlateDisplay = TaxiPlate.Format(r.NumberPlate),
            CityId = r.CityId,
            CityName = r.CityName,
            BaseLat = r.BaseLat,
            BaseLng = r.BaseLng,
            LocalDayRate = r.LocalDayRate,
            PriorityRank = r.PriorityRank,
            IsFeatured = r.IsFeatured,
            TripReplyLabel = TaxiTripReplyLabels.Of(r.TripLeadsReceived, r.TripLeadsAnsweredInApp),
            FromName = r.FromName,
            FromLat = r.FromLat,
            FromLng = r.FromLng,
            ToName = r.ToName,
            ToLat = r.ToLat,
            ToLng = r.ToLng,
            Fare = r.Fare,
            TripType = r.TripType,
            GeometryJson = r.GeometryJson,
            DistanceKm = r.DistanceKm,
            Minutes = r.Minutes,
        }).ToList();
    }
}
