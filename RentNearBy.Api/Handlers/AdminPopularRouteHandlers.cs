using Microsoft.AspNetCore.Mvc;
using RentNearBy.Api.Handlers.Owner;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Infrastructure.Services.Owner.Be1;
using StackExchange.Redis;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

// Contract 1.6 AD1-AD5 (policy AdminOnly). The admin sends city keys only; names are the server's snapshot. Every successful
// write bumps the popular generation after commit.
public static class AdminPopularRouteHandlers
{
    public static async Task<IResult> List(PopularRouteAdminStore store, CancellationToken ct)
        => OkResponse(new PopularRoutesAdminDto(await store.ListAsync(ct)));

    public static async Task<IResult> Create(
        PopularRouteCreateRequest? request, PopularRouteAdminStore store, [FromServices] IConnectionMultiplexer? redis, CancellationToken ct)
    {
        var result = await store.CreateAsync(request?.FromCityKey, request?.ToCityKey, ct);
        if (!result.Ok) return Refusal(result.Error!);
        await BumpAsync(redis);
        return CreatedResponse(result.Value!, $"/api/v1/admin/taxis/popular-routes/{result.Value!.Id}");
    }

    public static async Task<IResult> Delete(Guid id, PopularRouteAdminStore store, [FromServices] IConnectionMultiplexer? redis, CancellationToken ct)
    {
        await store.DeleteAsync(id, ct);
        await BumpAsync(redis);
        return NoContentResponse();
    }

    public static async Task<IResult> SetActive(
        Guid id, PopularRouteActiveRequest? request, PopularRouteAdminStore store, [FromServices] IConnectionMultiplexer? redis, CancellationToken ct)
    {
        if (request?.IsActive is not { } active) return BadRequestResponse("Invalid request.");
        var row = await store.SetActiveAsync(id, active, ct);
        if (row == null) return NotFoundResponse("Route not found.");
        await BumpAsync(redis);
        return OkResponse(row);
    }

    public static async Task<IResult> Reorder(
        PopularRouteOrderRequest? request, PopularRouteAdminStore store, [FromServices] IConnectionMultiplexer? redis, CancellationToken ct)
    {
        var result = await store.ReorderAsync(request?.Ids, ct);
        if (!result.Ok) return Refusal(result.Error!);
        await BumpAsync(redis);
        return OkResponse(new PopularRoutesAdminDto(result.Value!));
    }

    private static IResult Refusal(Be1Error error) => error.Type == TripErrorTypes.CitiesUnavailable
        ? ErrorResponse(503, error.Message, error.Type)
        : OwnerHttp.Refuse(error.Type, error.Message, error.Data);

    private static Task BumpAsync(IConnectionMultiplexer? redis) => FeedCache.BumpAsync(redis, [ListingCacheKeys.TaxiPopularGen]);
}
