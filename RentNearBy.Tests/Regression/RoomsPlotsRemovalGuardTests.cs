using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using RentNearBy.Api.Endpoints;
using RentNearBy.Api.Handlers;
using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.Regression;

// Guards from CONTRACT_REMOVE_RP section 5: the kept Stay/Taxi/Trip behaviour is unchanged after the Rooms/Plots removal.
public class RoomsPlotsRemovalGuardTests
{
    private static readonly Guid RetiredRentOwnerId = new("10000000-0000-0000-0000-000000000002");
    private static readonly Guid RetiredPropertyOwnerId = new("10000000-0000-0000-0000-000000000003");

    // G1
    [Fact]
    public void LocationsHandlers_GetContext_KeepsSignature()
    {
        var method = typeof(LocationsHandlers).GetMethod(nameof(LocationsHandlers.GetContext), BindingFlags.Public | BindingFlags.Static);

        Assert.NotNull(method);
        Assert.Equal(typeof(Task<IResult>), method.ReturnType);
        var parameters = method.GetParameters();
        Assert.Equal(new[] { "lat", "lng", "sp", "includeAddress" }, parameters.Select(p => p.Name));
        Assert.Equal(new[] { typeof(double), typeof(double), typeof(IServiceProvider), typeof(bool) }, parameters.Select(p => p.ParameterType));
        Assert.True(parameters[3].HasDefaultValue);
        Assert.False((bool)parameters[3].DefaultValue!);
    }

    [Fact]
    public async Task LocationsEndpoints_Context_MapsToLocationsHandlersGetContext()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddMemoryCache();
        builder.Services.AddScoped<IUnitOfWork>(_ => throw new InvalidOperationException("not used"));
        builder.Services.AddScoped<ApplicationDbContext>(_ => throw new InvalidOperationException("not used"));
        await using var app = builder.Build();

        app.MapGroup("/api/v1/locations").MapLocationsEndpoints();

        var getContext = typeof(LocationsHandlers).GetMethod(nameof(LocationsHandlers.GetContext))!;
        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(s => s.Endpoints)
            .OfType<RouteEndpoint>()
            .ToList();

        var context = Assert.Single(endpoints, e => e.Metadata.GetMetadata<MethodInfo>() == getContext);
        Assert.Equal("/api/v1/locations/context", "/" + context.RoutePattern.RawText!.Trim('/'));
        var methods = context.Metadata.GetMetadata<IHttpMethodMetadata>();
        Assert.NotNull(methods);
        Assert.Equal(new[] { "GET" }, methods.HttpMethods);
    }

    // G2
    [Fact]
    public void PlanFeatures_All_IsStayAndTaxi()
    {
        Assert.Equal(new[] { ListingKinds.Stay, ListingKinds.Taxi }, PlanFeatures.All);
        Assert.True(PlanFeatures.IsValid(ListingKinds.Stay));
        Assert.True(PlanFeatures.IsValid(ListingKinds.Taxi));
    }

    [Theory]
    [InlineData("Room")]
    [InlineData("Plot")]
    [InlineData("Both")]
    [InlineData("")]
    [InlineData(null)]
    public void PlanFeatures_IsValid_RemovedKindsFalse(string? featureKey)
        => Assert.False(PlanFeatures.IsValid(featureKey));

    [Fact]
    public void PlanFeatureKeys_Unchanged()
        => Assert.Equal(new[] { PlanFeatureKeys.ListingLimit, PlanFeatureKeys.Priority, PlanFeatureKeys.Featured, PlanFeatureKeys.JobsPosts }, PlanFeatureKeys.All);

    [Fact]
    public void LiveSlotService_KeepsOnlyRankMethods()
    {
        var names = typeof(ILiveSlotService).GetMethods().Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal);

        Assert.Equal(
            new[] { "ApplyGrantEffectsAsync", "ExpireEffectsAsync", "LockOwnerAsync", "ReconcileRanksAsync", "SetRankAsync" },
            names);
        Assert.True(typeof(ILiveSlotService).IsAssignableFrom(typeof(PlanSlotService)));
    }

    [Fact]
    public void LiveSlotService_KeptSignatures()
    {
        var t = typeof(ILiveSlotService);

        Assert.Equal(typeof(Task), t.GetMethod(nameof(ILiveSlotService.LockOwnerAsync))!.ReturnType);
        Assert.Equal(typeof(Task<int>), t.GetMethod(nameof(ILiveSlotService.SetRankAsync))!.ReturnType);
        Assert.Equal(
            new[] { typeof(Guid), typeof(string), typeof(short), typeof(bool) },
            t.GetMethod(nameof(ILiveSlotService.SetRankAsync))!.GetParameters().Select(p => p.ParameterType));
        Assert.Equal(typeof(Task), t.GetMethod(nameof(ILiveSlotService.ApplyGrantEffectsAsync))!.ReturnType);
        Assert.Equal(typeof(Task<ExpireEffectsResult>), t.GetMethod(nameof(ILiveSlotService.ExpireEffectsAsync))!.ReturnType);
        Assert.Equal(typeof(Task<RankReconcileResult>), t.GetMethod(nameof(ILiveSlotService.ReconcileRanksAsync))!.ReturnType);
    }

    [Fact]
    public void PlanEntitlementService_KeepsLimitsReaders()
    {
        var names = typeof(IPlanEntitlementService).GetMethods().Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal);

        Assert.Equal(new[] { "GetLimitsAsync", "GetLimitsBatchAsync" }, names);
    }

    // G5
    [Fact]
    public void LiveSlotService_HasNoExtendValidUntil()
        => Assert.Null(typeof(ILiveSlotService).GetMethod("ExtendValidUntilAsync"));

    // G6
    [Fact]
    public void GoLiveRequestedWorkerService_StillAHostedWorker()
        => Assert.True(typeof(Microsoft.Extensions.Hosting.BackgroundService).IsAssignableFrom(typeof(GoLiveRequestedWorkerService)));

    // G7
    [Fact]
    public void AdminHandlers_SendGoLiveNotificationAsync_Kept()
    {
        var method = typeof(AdminHandlers).GetMethod("SendGoLiveNotificationAsync", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        Assert.True(method.IsAssembly);
        Assert.Equal(typeof(Task), method.ReturnType);
        Assert.Equal(4, method.GetParameters().Length);
        Assert.Equal(typeof(IRabbitMqPublisher), method.GetParameters()[1].ParameterType);
        Assert.Equal(typeof(NotificationEvent), method.GetParameters()[2].ParameterType);
    }

    // G8
    [Fact]
    public void CreateConversation_Stay_Valid()
    {
        var result = new CreateConversationRequestValidator().Validate(
            new CreateConversationRequest { ListingType = ListingKinds.Stay, ListingId = Guid.NewGuid() });

        ValidationAssert.Valid(result);
    }

    [Theory]
    [InlineData("Room")]
    [InlineData("Plot")]
    [InlineData("Both")]
    [InlineData("Taxi")]
    [InlineData("stay")]
    [InlineData("")]
    public void CreateConversation_OtherKinds_Rejected(string listingType)
    {
        var result = new CreateConversationRequestValidator().Validate(
            new CreateConversationRequest { ListingType = listingType, ListingId = Guid.NewGuid() });

        Assert.False(result.IsValid);
        ValidationAssert.HasMessage(result, "ListingType must be 'Stay'");
    }

    [Fact]
    public void ConversationDto_KeepsStayFields()
    {
        var t = typeof(ConversationDto);

        Assert.NotNull(t.GetProperty(nameof(ConversationDto.StayTypeId)));
        Assert.NotNull(t.GetProperty(nameof(ConversationDto.ListingTitle)));
        Assert.NotNull(t.GetProperty(nameof(ConversationDto.Area)));
        Assert.NotNull(t.GetProperty(nameof(ConversationDto.ListingThumbnailUrl)));
        Assert.NotNull(t.GetProperty(nameof(ConversationDto.Status)));
        Assert.NotNull(t.GetProperty(nameof(ConversationDto.TripEnquiry)));
        Assert.Null(t.GetProperty("RoomTypeId"));
        Assert.Null(t.GetProperty("PlotTypeId"));
    }

    // G9
    private static CreateQuestionTemplateRequest Template(string listingType, Guid? stayTypeId) => new()
    {
        Key = "is_available",
        ListingType = listingType,
        StayTypeId = stayTypeId,
        QuestionText = "Is it available?",
        AnswerOptionsJson = """[{"key":"yes","text":"Yes"},{"key":"no","text":"No"}]""",
    };

    [Fact]
    public void QuestionTemplate_DefaultListingType_IsStay()
    {
        Assert.Equal(ListingKinds.Stay, new CreateQuestionTemplateRequest().ListingType);
        Assert.Equal(ListingKinds.Stay, new QuestionTemplate().ListingType);
        Assert.Equal(ListingKinds.Stay, new QuestionTemplateDto().ListingType);
    }

    [Fact]
    public void QuestionTemplate_StayWithStayType_Valid()
        => ValidationAssert.Valid(new CreateQuestionTemplateRequestValidator().Validate(
            Template(ListingKinds.Stay, new Guid("e1000000-0000-0000-0000-000000000001"))));

    [Fact]
    public void QuestionTemplate_StayWithoutStayType_Valid()
        => ValidationAssert.Valid(new CreateQuestionTemplateRequestValidator().Validate(Template(ListingKinds.Stay, null)));

    [Theory]
    [InlineData("Room")]
    [InlineData("Plot")]
    [InlineData("Both")]
    [InlineData("")]
    public void QuestionTemplate_OtherScopes_Rejected(string listingType)
    {
        var result = new CreateQuestionTemplateRequestValidator().Validate(Template(listingType, null));

        Assert.False(result.IsValid);
        ValidationAssert.HasMessage(result, "ListingType must be 'Stay'");
    }

    [Fact]
    public void QuestionTemplate_StayTypeOnOtherScope_Rejected()
    {
        var result = new CreateQuestionTemplateRequestValidator().Validate(
            Template("Room", new Guid("e1000000-0000-0000-0000-000000000001")));

        ValidationAssert.HasMessage(result, "StayTypeId can only be set when ListingType is 'Stay'");
    }

    [Fact]
    public void QuestionTemplate_HasNoRoomOrPlotScope()
    {
        foreach (var t in new[] { typeof(QuestionTemplate), typeof(CreateQuestionTemplateRequest), typeof(QuestionTemplateDto) })
        {
            Assert.NotNull(t.GetProperty("StayTypeId"));
            Assert.Null(t.GetProperty("RoomTypeId"));
            Assert.Null(t.GetProperty("PlotTypeId"));
        }
    }

    // G11
    [Fact]
    public void AppTabKeys_KeptSets()
    {
        Assert.Equal(new[] { AppTabKeys.Home, AppTabKeys.Stays, AppTabKeys.Jobs, AppTabKeys.Taxi, AppTabKeys.Profile }, AppTabKeys.All);
        Assert.Equal(new[] { "HOME", "STAYS", "JOBS", "TAXI", "PROFILE" }, AppTabKeys.All);
        Assert.True(AppTabKeys.ApiGated.SetEquals([AppTabKeys.Stays, AppTabKeys.Jobs, AppTabKeys.Taxi]));
        Assert.True(AppTabKeys.NonDeactivatable.SetEquals([AppTabKeys.Home, AppTabKeys.Profile]));
    }

    [Theory]
    [InlineData("STAYS", "STAY_OWNER")]
    [InlineData("TAXI", "TAXI_DRIVER")]
    [InlineData("JOBS", null)]
    [InlineData("HOME", null)]
    [InlineData("PROFILE", null)]
    [InlineData("ROOMS", null)]
    [InlineData("PLOTS", null)]
    public void AppTabKeys_LinkedRoleKey(string tabKey, string? expected)
        => Assert.Equal(expected, AppTabKeys.LinkedRoleKey(tabKey));

    [Theory]
    [InlineData("Stay", "STAYS")]
    [InlineData("Taxi", "TAXI")]
    [InlineData("Room", null)]
    [InlineData("Plot", null)]
    [InlineData(null, null)]
    public void AppTabKeys_ForListingKind(string? kind, string? expected)
        => Assert.Equal(expected, AppTabKeys.ForListingKind(kind));

    // G12
    [Fact]
    public void UserRoles_KeptIdsUnchanged()
    {
        Assert.Equal(new Guid("10000000-0000-0000-0000-000000000001"), UserRoles.SeekerId);
        Assert.Equal(new Guid("10000000-0000-0000-0000-000000000004"), UserRoles.StayOwnerId);
        Assert.Equal(new Guid("10000000-0000-0000-0000-000000000005"), UserRoles.TaxiDriverId);
        Assert.Equal("SEEKER", UserRoles.Seeker);
        Assert.Equal("STAY_OWNER", UserRoles.StayOwner);
        Assert.Equal("TAXI_DRIVER", UserRoles.TaxiDriver);
    }

    [Fact]
    public void UserRoles_KeptMaps()
    {
        Assert.Equal(UserRoles.Seeker, UserRoles.KeyOf(UserRoles.SeekerId));
        Assert.Null(UserRoles.ListingKindOf(UserRoles.SeekerId));
        Assert.Null(UserRoles.TabKeyOf(UserRoles.SeekerId));

        Assert.Equal(UserRoles.StayOwner, UserRoles.KeyOf(UserRoles.StayOwnerId));
        Assert.Equal(ListingKinds.Stay, UserRoles.ListingKindOf(UserRoles.StayOwnerId));
        Assert.Equal(AppTabKeys.Stays, UserRoles.TabKeyOf(UserRoles.StayOwnerId));

        Assert.Equal(UserRoles.TaxiDriver, UserRoles.KeyOf(UserRoles.TaxiDriverId));
        Assert.Equal(ListingKinds.Taxi, UserRoles.ListingKindOf(UserRoles.TaxiDriverId));
        Assert.Equal(AppTabKeys.Taxi, UserRoles.TabKeyOf(UserRoles.TaxiDriverId));
    }

    [Fact]
    public void UserRoles_RetiredIdsAreUnknown()
    {
        foreach (var id in new[] { RetiredRentOwnerId, RetiredPropertyOwnerId })
        {
            Assert.Throws<InvalidOperationException>(() => UserRoles.KeyOf(id));
            Assert.Throws<InvalidOperationException>(() => UserRoles.ListingKindOf(id));
        }
    }

    [Theory]
    [InlineData("Stay", 3)]
    [InlineData("Taxi", 5)]
    [InlineData("Room", 0)]
    [InlineData("Plot", 0)]
    [InlineData("", 0)]
    public void BlockingListingCounts_ForKind(string kind, int expected)
        => Assert.Equal(expected, new BlockingListingCounts(Stays: 3, Taxis: 5).ForKind(kind));

    [Theory]
    [InlineData("Stay", "A stay that was approved or is under review blocks this role change.")]
    [InlineData("Taxi", "A taxi that was approved or is under review blocks this role change.")]
    [InlineData("Room", "A listing that was approved or is under review blocks this role change.")]
    public void RoleChangeRules_LiveListingBlockedMessage(string kind, string expected)
        => Assert.Equal(expected, RoleChangeRules.LiveListingBlockedMessage(kind));

    [Fact]
    public void RoleChangeRules_IsLocked_StayTaxiUnchanged()
    {
        Assert.True(RoleChangeRules.IsLocked(ListingKinds.Stay, everApproved: true));
        Assert.True(RoleChangeRules.IsLocked(ListingKinds.Stay, false, StayStatuses.PendingReview));
        Assert.True(RoleChangeRules.IsLocked(ListingKinds.Stay, false, StayStatuses.Approved));
        Assert.False(RoleChangeRules.IsLocked(ListingKinds.Stay, false, null));
        Assert.True(RoleChangeRules.IsLocked(ListingKinds.Taxi, everApproved: true));
        Assert.True(RoleChangeRules.IsLocked(ListingKinds.Taxi, false, TaxiStatuses.PendingReview));
        Assert.True(RoleChangeRules.IsLocked(ListingKinds.Taxi, false, TaxiStatuses.Approved));
        Assert.False(RoleChangeRules.IsLocked(ListingKinds.Taxi, false, null));
        Assert.False(RoleChangeRules.IsLocked("Room", everApproved: true));
        Assert.False(RoleChangeRules.IsLocked(null, everApproved: true));
    }

    [Fact]
    public void RoleChangeRules_KeptConstants()
    {
        Assert.Equal(7, RoleChangeRules.RejectedCooldownDays);
        Assert.Equal(30, RoleChangeRules.ApprovedCooldownDays);
        Assert.Equal("RoleChangeBlockedByLiveListing", RoleChangeRules.LiveListingErrorType);
    }

    // G13 (with the schema guard below: no Roles row for the retired keys, so a lookup gives InvalidRole)
    [Fact]
    public void Schema_RolesSeed_IsThreeKeptRoles()
    {
        using var db = DesignContext();
        var roles = DesignModel(db).FindEntityType(typeof(Role))!.GetSeedData().ToList();

        Assert.Equal(3, roles.Count);
        Assert.Equal(
            new[] { UserRoles.SeekerId, UserRoles.StayOwnerId, UserRoles.TaxiDriverId },
            roles.Select(r => (Guid)r[nameof(Role.Id)]!).OrderBy(g => g));
        Assert.Equal(
            new[] { "SEEKER", "STAY_OWNER", "TAXI_DRIVER" },
            roles.Select(r => (string)r[nameof(Role.Key)]!).OrderBy(k => k, StringComparer.Ordinal));
        var seeker = Assert.Single(roles, r => (Guid)r[nameof(Role.Id)]! == UserRoles.SeekerId);
        Assert.Equal("Plan trips, find stays and taxis", seeker[nameof(Role.Description)]);
    }

    // Section 6
    [Fact]
    public void Schema_HasNoRoomOrPlotTables()
    {
        using var db = DesignContext();
        var tables = DesignModel(db).GetEntityTypes().Select(e => e.GetTableName()).Where(n => n != null).ToHashSet();

        foreach (var removed in new[] { "RoomListings", "RoomPhotos", "RoomTypes", "PlotListings", "PlotPhotos", "PlotTypes" })
            Assert.DoesNotContain(removed, tables);
        foreach (var kept in new[] { "Users", "Roles", "Conversations", "ListingReports", "QuestionTemplates", "StayListings", "StayRooms", "TaxiListings", "AppTabs", "Plans", "UserPlans", "DistrictBanners", "Trips", "TripEnquiries" })
            Assert.Contains(kept, tables);
    }

    [Fact]
    public void Schema_KeptColumns()
    {
        using var db = DesignContext();
        var model = DesignModel(db);

        Assert.NotNull(model.FindEntityType(typeof(StayRoom))!.FindProperty(nameof(StayRoom.Floor)));
        var template = model.FindEntityType(typeof(QuestionTemplate))!;
        Assert.NotNull(template.FindProperty("StayTypeId"));
        Assert.Null(template.FindProperty("RoomTypeId"));
        Assert.Null(template.FindProperty("PlotTypeId"));
    }

    private static ApplicationDbContext DesignContext()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=localhost;Database=design_only", o => o.UseNetTopologySuite())
            .Options);

    private static IModel DesignModel(ApplicationDbContext db) => db.GetService<IDesignTimeModel>().Model;

    // G14
    [Theory]
    [InlineData("Stay", "/my-stays")]
    [InlineData("Taxi", "/my-taxi")]
    [InlineData("Room", "/notifications")]
    [InlineData("Plot", "/notifications")]
    [InlineData("Anything", "/notifications")]
    public void UserNotificationRoutes_ForListingKind(string kind, string expected)
        => Assert.Equal(expected, UserNotificationRoutes.ForListingKind(kind));

    [Fact]
    public void UserNotificationRoutes_OwnedByKind()
    {
        Assert.Equal(new[] { "/my-stays", "/stay-enquiries" }, UserNotificationRoutes.OwnedByKind(ListingKinds.Stay));
        Assert.Equal(new[] { "/my-taxi", "/trip-leads" }, UserNotificationRoutes.OwnedByKind(ListingKinds.Taxi));
        Assert.Empty(UserNotificationRoutes.OwnedByKind("Room"));
        Assert.Empty(UserNotificationRoutes.OwnedByKind("Plot"));
    }

    [Theory]
    [InlineData("RoleRequestApproved", "role_request_approved")]
    [InlineData("StayApproved", "stay_approved")]
    [InlineData("TaxiRejected", "taxi_rejected")]
    [InlineData("PlanActivated", "plan_activated")]
    [InlineData("TripEnquiryReceived", "trip_enquiry_received")]
    public void NotificationTypes_KeptWireValues(string type, string wire)
        => Assert.Equal(wire, NotificationTypes.ToWireValue(type));

    [Fact]
    public void NotificationTypes_GoLiveTypesGone()
    {
        Assert.Null(typeof(NotificationTypes).GetField("GoLiveApproved"));
        Assert.Null(typeof(NotificationTypes).GetField("GoLiveRejected"));
        Assert.Equal("GoLiveApproved", NotificationTypes.ToWireValue("GoLiveApproved"));
    }

    // G16
    [Fact]
    public void ListingCacheKeys_ContextPatterns_OnlyContext()
    {
        Assert.Equal(new[] { "context:*" }, ListingCacheKeys.ContextPatterns);
        var id = new Guid("11111111-2222-3333-4444-555555555555");
        Assert.Equal("context:district:11111111222233334444555555555555", ListingCacheKeys.ContextDistrict(id));
    }

    // G22 (backend side)
    [Fact]
    public void HomeFeedDto_KeepsStayTaxiSections()
    {
        Assert.Equal(
            new[] { "RecentStays", "Stays", "Tabs", "Taxis" },
            typeof(HomeFeedDto).GetProperties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(
            new[] { "Jobs", "Stays", "Taxi" },
            typeof(HomeFeedTabsDto).GetProperties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
    }
}
