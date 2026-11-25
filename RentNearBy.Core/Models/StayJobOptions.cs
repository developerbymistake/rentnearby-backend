namespace RentNearBy.Core.Models;

public static class StayJobStatuses
{
    public const string Active = "Active";
    // Kept but off browse after Plus ends: the newest JobsPosts open jobs stay Active (PlanLimitsReconcile); Close and Delete only.
    public const string Inactive = "Inactive";
    public const string Closed = "Closed";

    public static readonly string[] All = [Active, Inactive, Closed];
}

public static class StayJobRules
{
    public const int TitleMin = 3;
    public const int TitleMax = 100;
    public const int DescriptionMax = 2000;
    public const int MinSalary = 100;
    public const int MaxSalary = 9_900_000;
    public const int MinExperienceYears = 0;
    public const int MaxExperienceYears = 20;
    public const int LookupNameMax = 80;
    public const int PhoneLength = 10;
    public const string PhonePattern = @"^[6-9]\d{9}$";

    // The cap comes from the owner's plan (PlanFeatureKeys.JobsPosts); the two lines of doc 8.8 (upgrade possible or not).
    public static string LimitMessage(int maxActive, bool canUpgrade)
        => Owner.OwnerErrorTypes.LimitMessage(Owner.OwnerErrorTypes.StayJobLimit, maxActive, canUpgrade);

    // error.data of StayJobLimit: the one limit shape { key, limit, used, canUpgrade } plus the old "active" field.
    public static StayJobLimitData LimitData(int maxActive, int active, bool canUpgrade)
        => new(PlanFeatureKeys.JobsPosts, maxActive, active, canUpgrade, active);
}

public sealed record StayJobLimitData(string Key, int Limit, int Used, bool CanUpgrade, int Active);

public static class StayJobErrorTypes
{
    public const string StayJobLimit = "StayJobLimit";
    public const string StayJobClosed = "StayJobClosed";
    public const string InvalidJobLookup = "InvalidJobLookup";
    public const string JobLookupInUse = "JobLookupInUse";
}

public static class StayJobLookupKinds
{
    public const string Departments = "departments";
    public const string Roles = "roles";
    public const string JobTypes = "job-types";
    public const string ShiftTypes = "shift-types";
    public const string SalaryTypes = "salary-types";

    public static readonly string[] All = [Departments, Roles, JobTypes, ShiftTypes, SalaryTypes];
}
