using FluentValidation;
using Mapster;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

// Agent CRUD + photo + category bulk-set. Entirely an admin-managed concern — the consumer app never
// calls this directly, it only ever sees an Agent embedded inside InquiryDetailDto.AssignedAgent.
public static class AgentHandlers
{
    private const long MaxImageBytes = 10 * 1024 * 1024;

    // serviceCategoryId provided -> category-scoped, active-only picker (admin's inquiry-assign flow).
    // Omitted -> full admin list (all statuses), matches the flat-route optional-query-param convention.
    public static async Task<IResult> GetAgents(Guid? serviceCategoryId, IUnitOfWork unitOfWork)
    {
        var agents = serviceCategoryId.HasValue
            ? await unitOfWork.Agents.GetActiveByServiceCategoryIdAsync(serviceCategoryId.Value)
            : await unitOfWork.Agents.GetAllWithCategoriesAsync();
        return OkResponse(agents.Select(a => a.Adapt<AgentDto>()));
    }

    public static async Task<IResult> GetAgentById(Guid id, IUnitOfWork unitOfWork)
    {
        var agent = await unitOfWork.Agents.GetByIdWithCategoriesAsync(id);
        if (agent == null) return NotFoundResponse("Agent not found");
        return OkResponse(agent.Adapt<AgentDto>());
    }

    public static async Task<IResult> AdminCreateAgent(
        CreateAgentRequest request, IValidator<CreateAgentRequest> validator, IUnitOfWork unitOfWork)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var agent = new Agent
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            Phone = request.Phone.Trim(),
            WhatsAppNumber = request.WhatsAppNumber.Trim(),
            PhotoUrl = string.Empty,
            PhotoFilePath = string.Empty,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };

        await unitOfWork.Agents.AddAsync(agent);
        await unitOfWork.SaveChangesAsync();

        return CreatedResponse(agent.Adapt<AgentDto>(), $"/api/v1/agents/{agent.Id}");
    }

    public static async Task<IResult> AdminUpdateAgent(
        Guid id, UpdateAgentRequest request, IValidator<UpdateAgentRequest> validator, IUnitOfWork unitOfWork)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var agent = await unitOfWork.Agents.GetByIdAsync(id);
        if (agent == null) return NotFoundResponse("Agent not found");

        if (request.Name != null) agent.Name = request.Name.Trim();
        if (request.Phone != null) agent.Phone = request.Phone.Trim();
        if (request.WhatsAppNumber != null) agent.WhatsAppNumber = request.WhatsAppNumber.Trim();
        if (request.IsActive.HasValue) agent.IsActive = request.IsActive.Value;

        await unitOfWork.SaveChangesAsync();

        var updated = await unitOfWork.Agents.GetByIdWithCategoriesAsync(id);
        return OkResponse(updated!.Adapt<AgentDto>());
    }

    public static async Task<IResult> AdminDeleteAgent(
        Guid id, IUnitOfWork unitOfWork, IPhotoService photoService)
    {
        var agent = await unitOfWork.Agents.GetByIdAsync(id);
        if (agent == null) return NotFoundResponse("Agent not found");

        // AssignedAgentId's FK is SetNull (unlike Service/Package's Restrict), so the raw DB delete
        // would succeed silently even with referencing rows — this is a pure business-level guard
        // restricted to "live" inquiries, matching IInquiryRepository.ExistsByAssignedAgentIdAsync.
        if (await unitOfWork.Inquiries.ExistsByAssignedAgentIdAsync(id))
            return ConflictResponse("Cannot delete an agent currently assigned to a live inquiry. Reassign or resolve those inquiries first.");

        if (!string.IsNullOrEmpty(agent.PhotoFilePath))
            await photoService.DeletePhotoAsync(agent.PhotoFilePath);

        await unitOfWork.Agents.DeleteAsync(agent);
        await unitOfWork.SaveChangesAsync();
        return NoContentResponse();
    }

    public static async Task<IResult> AdminUploadAgentPhoto(
        Guid id, IFormFile image, IUnitOfWork unitOfWork, IPhotoService photoService)
    {
        var agent = await unitOfWork.Agents.GetByIdAsync(id);
        if (agent == null) return NotFoundResponse("Agent not found");
        if (image.Length > MaxImageBytes) return BadRequestResponse("Image size must not exceed 10MB");

        if (!string.IsNullOrEmpty(agent.PhotoFilePath))
            await photoService.DeletePhotoAsync(agent.PhotoFilePath);

        using var stream = image.OpenReadStream();
        var (url, filePath) = await photoService.SaveAgentPhotoAsync(stream, image.FileName, id);

        agent.PhotoUrl = url;
        agent.PhotoFilePath = filePath;
        await unitOfWork.SaveChangesAsync();

        return OkResponse(new { photoUrl = url });
    }

    public static async Task<IResult> AdminDeleteAgentPhoto(
        Guid id, IUnitOfWork unitOfWork, IPhotoService photoService)
    {
        var agent = await unitOfWork.Agents.GetByIdAsync(id);
        if (agent == null) return NotFoundResponse("Agent not found");
        if (string.IsNullOrEmpty(agent.PhotoFilePath)) return BadRequestResponse("No photo to delete");

        await photoService.DeletePhotoAsync(agent.PhotoFilePath);
        agent.PhotoUrl = string.Empty;
        agent.PhotoFilePath = string.Empty;
        await unitOfWork.SaveChangesAsync();

        return NoContentResponse();
    }

    public static async Task<IResult> AdminSetAgentCategories(
        Guid id, SetAgentCategoriesRequest request, IValidator<SetAgentCategoriesRequest> validator,
        IUnitOfWork unitOfWork, ApplicationDbContext db)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var agent = await unitOfWork.Agents.GetByIdAsync(id);
        if (agent == null) return NotFoundResponse("Agent not found");

        var distinctIds = request.ServiceCategoryIds.Distinct().ToList();
        if (distinctIds.Count > 0)
        {
            var validCount = await db.ServiceCategories.CountAsync(c => distinctIds.Contains(c.Id));
            if (validCount != distinctIds.Count)
                return BadRequestResponse("One or more ServiceCategoryIds are invalid");
        }

        // Full-replace, exact mirror of how BannerHandlers manipulates db.BannerDismissals directly.
        var existing = db.AgentServiceCategories.Where(ac => ac.AgentId == id);
        db.AgentServiceCategories.RemoveRange(existing);
        db.AgentServiceCategories.AddRange(distinctIds.Select(categoryId => new AgentServiceCategory
        {
            AgentId = id,
            ServiceCategoryId = categoryId,
        }));
        await db.SaveChangesAsync();

        var updated = await unitOfWork.Agents.GetByIdWithCategoriesAsync(id);
        return OkResponse(updated!.Adapt<AgentDto>());
    }
}
