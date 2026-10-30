using RentNearBy.Infrastructure.Data;
using RentNearBy.Tests.Data;

namespace RentNearBy.Tests.Integration;

// Derive from this and mark tests [IntegrationFact] / [IntegrationTheory].
[Collection(IntegrationCollection.Name)]
[Trait("Category", "Integration")]
public abstract class IntegrationTestBase(TestDatabase db)
{
    protected TestDatabase Db { get; } = db;

    protected ApplicationDbContext CreateContext() => Db.CreateContext();
}
