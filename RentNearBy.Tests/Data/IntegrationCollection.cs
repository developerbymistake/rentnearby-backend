namespace RentNearBy.Tests.Data;

// One shared database fixture; classes in this collection run one after another.
[CollectionDefinition(Name)]
public sealed class IntegrationCollection : ICollectionFixture<TestDatabase>
{
    public const string Name = "Integration";
}
