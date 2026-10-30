using System.Runtime.CompilerServices;
using RentNearBy.Tests.Data;

namespace RentNearBy.Tests.Integration;

public sealed class IntegrationFactAttribute : FactAttribute
{
    public IntegrationFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = TestDatabase.SkipReason;
        SkipUnless = nameof(TestDatabase.IsAvailable);
        SkipType = typeof(TestDatabase);
    }
}

public sealed class IntegrationTheoryAttribute : TheoryAttribute
{
    public IntegrationTheoryAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = TestDatabase.SkipReason;
        SkipUnless = nameof(TestDatabase.IsAvailable);
        SkipType = typeof(TestDatabase);
    }
}
