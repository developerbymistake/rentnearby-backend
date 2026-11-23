using RentNearBy.Api.Mappings;

namespace RentNearBy.Tests.Support;

// Mapster's global config may be set only before the first Adapt; test classes run in parallel, so it is set once here.
public static class MappingsOnce
{
    private static readonly Lazy<bool> Done = new(() =>
    {
        DtoMappings.ConfigureMappings();
        return true;
    });

    public static void Ensure() => _ = Done.Value;
}
