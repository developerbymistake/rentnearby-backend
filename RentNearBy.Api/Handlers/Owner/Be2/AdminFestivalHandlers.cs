using FluentValidation;
using RentNearBy.Core.DTOs.Owner;
using RentNearBy.Infrastructure.Services.Owner.Be2;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers.Owner.Be2;

// A2 /api/v1/admin/festivals (AdminOnly). Every write drops the festival cache; saving never touches an owner's rule.
public static class AdminFestivalHandlers
{
    public static async Task<IResult> List(FestivalService service, CancellationToken ct, string? cursor = null)
    {
        if (!OwnerHttp.TryDateCursor(cursor, out var after)) return OwnerHttp.BadCursor();
        return OkResponse(await service.PageAsync(after, ct));
    }

    public static async Task<IResult> States(FestivalService service, CancellationToken ct)
        => OkResponse(await service.StatesAsync(ct));

    public static async Task<IResult> Create(
        AdminFestivalRequest request, IValidator<AdminFestivalRequest> validator, FestivalService service, HttpContext http, CancellationToken ct)
    {
        if (request.Id == Guid.Empty) return BadRequestResponse("Please try again.");
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid) return OwnerHttp.Invalid(validation);
        var result = await service.CreateAsync(request, ct);
        if (!result.Ok) return Be2Answers.Error(result);
        AdminActionLog.Write(http, "festival.create", request.Id);
        return result.Created ? CreatedResponse(result.Value, $"/api/v1/admin/festivals/{result.Value!.Id}") : OkResponse(result.Value);
    }

    public static async Task<IResult> Update(
        Guid id, AdminFestivalRequest request, IValidator<AdminFestivalRequest> validator, FestivalService service, HttpContext http, CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid) return OwnerHttp.Invalid(validation);
        var result = await service.UpdateAsync(id, request, ct);
        if (result.Ok) AdminActionLog.Write(http, "festival.update", id);
        return Be2Answers.Of(result);
    }

    public static async Task<IResult> Delete(Guid id, FestivalService service, HttpContext http, CancellationToken ct)
    {
        await service.DeleteAsync(id, ct);
        AdminActionLog.Write(http, "festival.delete", id);
        return NoContentResponse();
    }
}
