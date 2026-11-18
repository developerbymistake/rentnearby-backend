using Microsoft.AspNetCore.Http;
using RentNearBy.Api.Filters;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;

namespace RentNearBy.Tests.TaxiRequests;

public class TripExecutionGateFilterTests
{
    private sealed class StubReader(bool enabled) : ITripExecutionFeatureReader
    {
        public int Reads { get; private set; }

        public Task<bool> IsEnabledAsync()
        {
            Reads++;
            return Task.FromResult(enabled);
        }

        public void Invalidate() { }
    }

    private static EndpointFilterInvocationContext Context() => EndpointFilterInvocationContext.Create(new DefaultHttpContext());

    [Fact]
    public async Task Gate_off_returns_403_trip_execution_disabled()
    {
        var reader = new StubReader(false);
        var nextCalled = false;

        var result = await new TripExecutionGateFilter(reader).InvokeAsync(Context(), _ =>
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>("handler");
        });

        Assert.False(nextCalled);
        Assert.Equal(1, reader.Reads);
        Assert.Equal(403, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        var body = Assert.IsType<ApiResponse<object>>(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
        Assert.Equal(403, body.Code);
        Assert.Null(body.Data);
        Assert.Equal(TripEnquiryErrorTypes.TripExecutionDisabled, body.Error!.Type);
        Assert.Equal("TripExecutionDisabled", body.Error.Type);
        Assert.Equal("Trip bookings are paused.", body.Error.Message);
    }

    [Fact]
    public async Task Gate_on_calls_next()
    {
        var reader = new StubReader(true);
        var context = Context();
        EndpointFilterInvocationContext? seen = null;

        var result = await new TripExecutionGateFilter(reader).InvokeAsync(context, c =>
        {
            seen = c;
            return ValueTask.FromResult<object?>("handler");
        });

        Assert.Equal("handler", result);
        Assert.Same(context, seen);
        Assert.Equal(1, reader.Reads);
    }
}
