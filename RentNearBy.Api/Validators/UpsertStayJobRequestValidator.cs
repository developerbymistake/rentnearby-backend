using FluentValidation;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.Models;

namespace RentNearBy.Api.Validators;

public class UpsertStayJobRequestValidator : AbstractValidator<UpsertStayJobRequest>
{
    public UpsertStayJobRequestValidator()
    {
        RuleFor(x => x.Title)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Job title is required")
            .Must(t => t.Trim().Length >= StayJobRules.TitleMin && t.Trim().Length <= StayJobRules.TitleMax)
            .WithMessage($"Job title must be {StayJobRules.TitleMin} to {StayJobRules.TitleMax} characters");

        RuleFor(x => x.Description)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Description is required")
            .MaximumLength(StayJobRules.DescriptionMax)
            .WithMessage($"Description must be at most {StayJobRules.DescriptionMax} characters");

        RuleFor(x => x.JobDepartmentId).NotEmpty().WithMessage("Pick a department");
        RuleFor(x => x.JobRoleId).NotEmpty().WithMessage("Pick a role");
        RuleFor(x => x.JobExperienceLevelId).NotEmpty().WithMessage("Pick an experience level");

        RuleFor(x => x.JobTypeId).NotEqual(Guid.Empty).When(x => x.JobTypeId.HasValue).WithMessage("Pick a valid job type");
        RuleFor(x => x.JobShiftTypeId).NotEqual(Guid.Empty).When(x => x.JobShiftTypeId.HasValue).WithMessage("Pick a valid shift");
        RuleFor(x => x.JobSalaryTypeId).NotEqual(Guid.Empty).When(x => x.JobSalaryTypeId.HasValue).WithMessage("Pick a valid salary type");

        RuleFor(x => x.MinimumExperienceYears)
            .InclusiveBetween(StayJobRules.MinExperienceYears, StayJobRules.MaxExperienceYears)
            .When(x => x.MinimumExperienceYears.HasValue)
            .WithMessage($"Min experience must be {StayJobRules.MinExperienceYears} to {StayJobRules.MaxExperienceYears} years");

        RuleFor(x => x.MinSalary)
            .InclusiveBetween(StayJobRules.MinSalary, StayJobRules.MaxSalary)
            .WithMessage("Min salary must be between ₹100 and ₹99,00,000");

        RuleFor(x => x.MaxSalary)
            .InclusiveBetween(StayJobRules.MinSalary, StayJobRules.MaxSalary)
            .WithMessage("Max salary must be between ₹100 and ₹99,00,000");

        RuleFor(x => x)
            .Must(x => x.MinSalary <= x.MaxSalary)
            .WithName(nameof(UpsertStayJobRequest.MaxSalary))
            .WithMessage("Max salary must be at least min salary.");
    }
}
