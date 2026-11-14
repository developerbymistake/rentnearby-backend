using System.Reflection;
using System.Security.Claims;
using FluentValidation.TestHelper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Api.Filters;
using RentNearBy.Api.Handlers;
using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Tests.Trips;

namespace RentNearBy.Tests.TripExecution;

public class TripExecutionSettingTests
{
    private sealed class CountingScopeFactory : IServiceScopeFactory
    {
        public int Scopes;
        public IServiceScope CreateScope()
        {
            Interlocked.Increment(ref Scopes);
            throw new InvalidOperationException("no db in unit tests");
        }
    }

    private sealed class StubReader(bool enabled) : ITripExecutionFeatureReader
    {
        public int Invalidations;
        public Task<bool> IsEnabledAsync() => Task.FromResult(enabled);
        public void Invalidate() => Invalidations++;
    }

    private sealed class FakeLoadReader(IMemoryCache cache, Func<Task<bool>>? load = null)
        : TripExecutionFeatureReader(cache, new CountingScopeFactory(), NullLogger<TripExecutionFeatureReader>.Instance)
    {
        public int Loads;
        public bool Stored;
        protected override async Task<bool> LoadAsync()
        {
            Interlocked.Increment(ref Loads);
            return load != null ? await load() : Stored;
        }
    }

    private static IMemoryCache NewCache() => new MemoryCache(new MemoryCacheOptions());

    [Fact]
    public async Task Off_value_is_cached_and_second_read_makes_no_load()
    {
        var reader = new FakeLoadReader(NewCache()) { Stored = false };
        Assert.False(await reader.IsEnabledAsync());
        Assert.False(await reader.IsEnabledAsync());
        Assert.Equal(1, reader.Loads);
    }

    [Fact]
    public async Task Invalidate_reloads_the_changed_value()
    {
        var reader = new FakeLoadReader(NewCache()) { Stored = false };
        Assert.False(await reader.IsEnabledAsync());
        reader.Stored = true;
        Assert.False(await reader.IsEnabledAsync());
        reader.Invalidate();
        Assert.True(await reader.IsEnabledAsync());
        Assert.True(await reader.IsEnabledAsync());
        Assert.Equal(2, reader.Loads);
    }

    [Fact]
    public async Task Load_started_before_invalidate_does_not_store_the_stale_value()
    {
        var gate = new TaskCompletionSource<bool>();
        var started = new TaskCompletionSource();
        var cache = NewCache();
        var first = true;
        var reader = new FakeLoadReader(cache, async () =>
        {
            if (!first) return true;
            first = false;
            started.SetResult();
            return await gate.Task;
        });

        var stale = reader.IsEnabledAsync();
        await started.Task;
        reader.Invalidate();
        gate.SetResult(false);
        Assert.False(await stale);
        Assert.False(cache.TryGetValue(ITripExecutionFeatureReader.CacheKey, out bool _));
        Assert.True(await reader.IsEnabledAsync());
    }

    private static async Task<object?> RunGate(ITripExecutionFeatureReader reader)
    {
        var ctx = EndpointFilterInvocationContext.Create(new DefaultHttpContext());
        return await new TripExecutionGateFilter(reader).InvokeAsync(ctx, _ => ValueTask.FromResult<object?>("next"));
    }

    [Fact]
    public async Task Gate_off_returns_403_with_type_and_message()
    {
        var result = Assert.IsAssignableFrom<IResult>(await RunGate(new StubReader(false)));
        Assert.Equal(403, ((IStatusCodeHttpResult)result).StatusCode);
        var body = (ApiResponse<object>)((IValueHttpResult)result).Value!;
        Assert.Equal(TripEnquiryErrorTypes.TripExecutionDisabled, body.Error!.Type);
        Assert.Equal("Trip bookings are paused.", body.Error.Message);
    }

    [Fact]
    public async Task Gate_on_calls_next()
        => Assert.Equal("next", await RunGate(new StubReader(true)));

    private static readonly UpdateTripExecutionRequestValidator Validator = new();

    [Fact]
    public void Validator_null_IsEnabled_rejected()
        => Validator.TestValidate(new UpdateTripExecutionRequest(null)).ShouldHaveValidationErrorFor(x => x.IsEnabled);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Validator_bool_accepted(bool value)
        => Validator.TestValidate(new UpdateTripExecutionRequest(value, "ok")).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void Validator_reason_over_500_rejected()
        => Validator.TestValidate(new UpdateTripExecutionRequest(true, new string('x', 501))).ShouldHaveValidationErrorFor(x => x.Reason);

    private static (IUnitOfWork Uow, List<string> Calls) Uow(AppFeatureFlag? flag)
    {
        var calls = new List<string>();
        var repo = TripTimerBatchLoadTests.Fake<IAppFeatureFlagRepository>.Create((m, a) =>
        {
            calls.Add(m.Name);
            return m.Name switch
            {
                nameof(IAppFeatureFlagRepository.GetByKeyAsync) => Task.FromResult(flag),
                "UpdateAsync" => Task.CompletedTask,
                _ => throw new InvalidOperationException(m.Name),
            };
        });
        var uow = TripTimerBatchLoadTests.Fake<IUnitOfWork>.Create((m, a) =>
        {
            calls.Add(m.Name);
            return m.Name switch
            {
                "get_AppFeatureFlags" => repo,
                nameof(IUnitOfWork.SaveChangesAsync) => Task.FromResult(1),
                _ => throw new InvalidOperationException(m.Name),
            };
        });
        return (uow, calls);
    }

    private static ClaimsPrincipal Admin(Guid id)
        => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id.ToString())], "t"));

    [Fact]
    public async Task Admin_put_updates_row_and_invalidates_after_save()
    {
        var flag = new AppFeatureFlag { FeatureKey = AppFeatureKeys.TripExecutionEnabled, IsEnabled = false };
        var (uow, calls) = Uow(flag);
        var reader = new StubReader(false);
        var admin = Guid.NewGuid();

        var result = await AdminHandlers.UpdateTripExecution(new UpdateTripExecutionRequest(true, "launch"), Validator,
            Admin(admin), uow, reader, NullLoggerFactory.Instance);

        Assert.Equal(200, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.True(flag.IsEnabled);
        Assert.Equal("launch", flag.Reason);
        Assert.Equal(admin, flag.UpdatedByAdminId);
        Assert.Equal(1, reader.Invalidations);
        Assert.Contains(nameof(IUnitOfWork.SaveChangesAsync), calls);
    }

    [Fact]
    public async Task Admin_put_invalid_request_is_400_and_does_not_invalidate()
    {
        var (uow, _) = Uow(new AppFeatureFlag());
        var reader = new StubReader(false);
        var result = await AdminHandlers.UpdateTripExecution(new UpdateTripExecutionRequest(null), Validator,
            Admin(Guid.NewGuid()), uow, reader, NullLoggerFactory.Instance);
        Assert.Equal(400, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.Equal(0, reader.Invalidations);
    }

    [Fact]
    public async Task Admin_put_missing_row_is_404()
    {
        var (uow, _) = Uow(null);
        var result = await AdminHandlers.UpdateTripExecution(new UpdateTripExecutionRequest(true), Validator,
            Admin(Guid.NewGuid()), uow, new StubReader(false), NullLoggerFactory.Instance);
        Assert.Equal(404, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Fact]
    public async Task Admin_get_returns_the_row()
    {
        var (uow, _) = Uow(new AppFeatureFlag { IsEnabled = true });
        var result = await AdminHandlers.GetTripExecution(uow);
        Assert.Equal(200, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Fact]
    public async Task Reminder_tick_returns_false_while_off_without_touching_the_db()
    {
        var scopes = new CountingScopeFactory();
        var service = new TripReminderService(scopes, new StubReader(false), NullLogger<TripReminderService>.Instance);
        Assert.False(await service.TickAsync(DateTime.UtcNow, CancellationToken.None));
        Assert.Equal(0, scopes.Scopes);
    }

    [Fact]
    public async Task Timer_pass_is_skipped_while_off()
    {
        var scopes = new CountingScopeFactory();
        var service = new TripTimerService(scopes, new StubReader(false), NullLogger<TripTimerService>.Instance);
        var run = typeof(TripTimerService).GetMethod("RunOnceAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)run.Invoke(service, [CancellationToken.None])!;
        Assert.Equal(0, scopes.Scopes);
    }
}
