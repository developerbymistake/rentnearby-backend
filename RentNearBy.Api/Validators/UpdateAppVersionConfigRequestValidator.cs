using FluentValidation;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.Models;

namespace RentNearBy.Api.Validators;

public class UpdateAppVersionConfigRequestValidator : AbstractValidator<UpdateAppVersionConfigRequest>
{
    public UpdateAppVersionConfigRequestValidator()
    {
        RuleFor(x => x.MinBuildNumber)
            .InclusiveBetween(AppVersionRules.MinBuildNumber, AppVersionRules.MaxBuildNumber)
            .When(x => x.MinBuildNumber.HasValue)
            .WithMessage($"Minimum build number must be between {AppVersionRules.MinBuildNumber} and {AppVersionRules.MaxBuildNumber}");

        RuleFor(x => x.LatestBuildNumber)
            .InclusiveBetween(AppVersionRules.MinBuildNumber, AppVersionRules.MaxBuildNumber)
            .When(x => x.LatestBuildNumber.HasValue)
            .WithMessage($"Latest build number must be between {AppVersionRules.MinBuildNumber} and {AppVersionRules.MaxBuildNumber}");

        RuleFor(x => x.MinVersionName)
            .Must(VersionNames.IsValid)
            .When(x => x.MinVersionName != null)
            .WithMessage("Minimum version name must look like 1.0.1 (2-4 numeric parts)");

        RuleFor(x => x.LatestVersionName)
            .Must(VersionNames.IsValid)
            .When(x => x.LatestVersionName != null)
            .WithMessage("Latest version name must look like 1.0.1 (2-4 numeric parts)");

        RuleFor(x => x.StoreUrl)
            .Must(BeAbsoluteHttpsUrl)
            .When(x => !string.IsNullOrWhiteSpace(x.StoreUrl))
            .WithMessage("Store URL must be an absolute https URL");

        RuleFor(x => x.StoreUrl)
            .Must(u => u!.Trim().Length <= AppVersionRules.MaxStoreUrlLength)
            .When(x => !string.IsNullOrWhiteSpace(x.StoreUrl))
            .WithMessage($"Store URL must be at most {AppVersionRules.MaxStoreUrlLength} characters");

        RuleFor(x => x.Message)
            .Must(m => m!.Trim().Length <= AppVersionRules.MaxMessageLength)
            .When(x => x.Message != null)
            .WithMessage($"Message must be at most {AppVersionRules.MaxMessageLength} characters");

        RuleFor(x => x.Reason)
            .Must(r => r!.Trim().Length <= AppVersionRules.MaxReasonLength)
            .When(x => x.Reason != null)
            .WithMessage($"Reason must be at most {AppVersionRules.MaxReasonLength} characters");

        RuleFor(x => x.ForceCacheMaxAgeHours)
            .InclusiveBetween(AppVersionRules.MinForceCacheMaxAgeHours, AppVersionRules.MaxForceCacheMaxAgeHours)
            .When(x => x.ForceCacheMaxAgeHours.HasValue)
            .WithMessage($"Force cache max age must be between {AppVersionRules.MinForceCacheMaxAgeHours} and {AppVersionRules.MaxForceCacheMaxAgeHours} hours");
    }

    private static bool BeAbsoluteHttpsUrl(string? url)
        => Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}
