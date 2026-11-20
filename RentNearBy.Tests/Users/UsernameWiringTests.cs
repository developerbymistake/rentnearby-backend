using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RentNearBy.Api.Extensions;
using RentNearBy.Api.Handlers;
using RentNearBy.Api.Mappings;
using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Infrastructure.Migrations;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Tests.TaxiRequests;

namespace RentNearBy.Tests.Users;

public class UsernameWiringTests
{
    private static (UsernameWorld World, IMemoryCache Cache, ClaimsPrincipal Principal, User User) Signed(string? username)
    {
        DtoMappings.ConfigureMappings();
        var w = new UsernameWorld();
        var user = new User { Id = Guid.NewGuid(), PhoneNumber = "9876543210", Name = "Asha", Username = username, IsPhoneVerified = true };
        w.Repo.Row = user;
        w.Repo.Stored[user.Id] = username;
        var cache = new MemoryCache(new MemoryCacheOptions());
        cache.Set(AppTabCache.Key, new List<AppTabDto>());
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())]));
        return (w, cache, principal, user);
    }

    private static UserDto Body(IResult result) => ((ApiResponse<UserDto>)((IValueHttpResult)result).Value!).Data!;

    [Fact]
    public async Task Profile_read_backfills_a_user_without_a_username()
    {
        var (w, cache, principal, user) = Signed(null);

        var dto = Body(await UsersHandlers.GetProfile(principal, w.Uow, cache, w.Service("Bakhli#48291736")));

        Assert.Equal("Bakhli#48291736", dto.Username);
        Assert.Equal("Bakhli#48291736", w.Repo.Stored[user.Id]);
    }

    [Fact]
    public async Task Second_profile_read_returns_the_same_username()
    {
        var (w, cache, principal, _) = Signed(null);
        var first = Body(await UsersHandlers.GetProfile(principal, w.Uow, cache, w.Service("Bakhli#48291736")));
        w.Repo.Row = new User { Id = w.Repo.Row!.Id, PhoneNumber = "9876543210", Username = w.Repo.Stored[w.Repo.Row.Id] };

        var second = Body(await UsersHandlers.GetProfile(principal, w.Uow, cache, w.Service("Bakhli#11111111")));

        Assert.Equal(first.Username, second.Username);
        Assert.Equal(1, w.Repo.SetCalls);
    }

    [Fact]
    public async Task Name_update_does_not_change_the_username()
    {
        var (w, cache, principal, user) = Signed("Bakhli#48291736");

        var dto = Body(await UsersHandlers.UpdateName(new UpdateNameRequest { Name = "Asha Rawat" }, principal,
            new UpdateNameRequestValidator(), w.Uow, cache, w.Service("Bakhli#11111111")));

        Assert.Equal("Asha Rawat", dto.Name);
        Assert.Equal("Bakhli#48291736", dto.Username);
        Assert.Equal("Bakhli#48291736", user.Username);
        Assert.Equal(0, w.Repo.SetCalls);
    }

    [Fact]
    public void Wire_field_is_username_on_the_user_and_the_login_response()
    {
        DtoMappings.ConfigureMappings();
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var user = Mapster.TypeAdapter.Adapt<UserDto>(new User { Id = Guid.NewGuid(), Username = "Bakhli#48291736" });

        using var userJson = JsonDocument.Parse(JsonSerializer.Serialize(user, web));
        Assert.Equal("Bakhli#48291736", userJson.RootElement.GetProperty("username").GetString());

        using var loginJson = JsonDocument.Parse(JsonSerializer.Serialize(
            new PhoneLoginResponse { NeedsOnboarding = true, Username = "Bakhli#48291736" }, web));
        Assert.True(loginJson.RootElement.GetProperty("needsOnboarding").GetBoolean());
        Assert.Equal("Bakhli#48291736", loginJson.RootElement.GetProperty("username").GetString());
    }

    [Fact]
    public void Snapshot_has_the_nullable_column_and_the_filtered_unique_index()
    {
        var entity = new ApplicationDbContextModelSnapshot().Model.FindEntityType(typeof(User).FullName!)!;
        var column = entity.FindProperty(nameof(User.Username))!;
        Assert.True(column.IsNullable);
        Assert.Equal(Usernames.MaxLength, column.GetMaxLength());

        var index = Assert.Single(entity.GetIndexes(), i => i.Properties.Any(p => p.Name == nameof(User.Username)));
        Assert.Equal(Usernames.UniqueIndex, index.Name);
        Assert.True(index.IsUnique);
        Assert.Equal("\"Username\" IS NOT NULL", index.GetFilter());
    }

    [Fact]
    public void Whole_row_update_of_a_stale_user_never_writes_the_username()
    {
        using var db = new RentNearBy.Infrastructure.Data.ApplicationDbContext(
            new DbContextOptionsBuilder<RentNearBy.Infrastructure.Data.ApplicationDbContext>()
                .UseNpgsql("Host=localhost;Database=design_only", o => o.UseNetTopologySuite())
                .Options);
        var stale = new User { Id = Guid.NewGuid(), PhoneNumber = "9876543210", Name = "Asha", Username = null };

        db.Users.Update(stale);

        var entry = db.Entry(stale);
        Assert.Equal(EntityState.Modified, entry.State);
        Assert.True(entry.Property(u => u.Name).IsModified);
        Assert.False(entry.Property(u => u.Username).IsModified);

        var fresh = new User { Id = Guid.NewGuid(), PhoneNumber = "9876543211", Username = "Bakhli#48291736" };
        db.Users.Add(fresh);
        Assert.Equal(Microsoft.EntityFrameworkCore.Metadata.PropertySaveBehavior.Save,
            db.Entry(fresh).Property(u => u.Username).Metadata.GetBeforeSaveBehavior());
    }

    [Fact]
    public void Container_resolves_the_username_service_and_the_memory_store_without_redis()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DATABASE_URL"] = "Host=localhost;Database=test;Username=test;Password=test",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApplicationServices(configuration);

        Assert.Equal(typeof(UsernameService), Assert.Single(services, d => d.ServiceType == typeof(IUsernameService)).ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, services.Single(d => d.ServiceType == typeof(IUsernameService)).Lifetime);
        Assert.Equal(typeof(MemoryPendingUsernameStore), Assert.Single(services, d => d.ServiceType == typeof(IPendingUsernameStore)).ImplementationType);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.IsType<UsernameService>(scope.ServiceProvider.GetRequiredService<IUsernameService>());
    }

    [Fact]
    public void Only_the_username_service_writes_the_username()
    {
        var assign = new Regex(@"\.Username\s*=(?!=)");
        var setProperty = new Regex(@"SetProperty\(\s*\w+\s*=>\s*\w+\.Username");
        var root = TaxiSource.Root();
        var writers = TaxiSource.ProductionFiles()
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"))
            .Where(f => { var s = File.ReadAllText(f); return assign.IsMatch(s) || setProperty.IsMatch(s); })
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .OrderBy(f => f)
            .ToList();

        Assert.Equal(
        [
            "RentNearBy.Infrastructure/Repositories/UserRepository.cs",
            "RentNearBy.Infrastructure/Services/UsernameService.cs",
        ], writers);
        Assert.Contains("u.Id == userId && u.Username == null",
            File.ReadAllText(Path.Combine(root, "RentNearBy.Infrastructure", "Repositories", "UserRepository.cs")));
    }
}
