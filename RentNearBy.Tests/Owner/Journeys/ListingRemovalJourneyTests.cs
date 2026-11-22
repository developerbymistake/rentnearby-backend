using RentNearBy.Tests.Trips;

namespace RentNearBy.Tests.Owner.Journeys;

// JT-B-LC-1 (FB-09, FB-20, FB-51): one soft delete for owner and admin; takedown drops a waiting live edit.
public class ListingRemovalJourneyTests
{
    private static string Src(params string[] parts) => File.ReadAllText(Path.Combine([CompletionCounterGuardTests.Root(), .. parts]));

    private static string Method(string src, string signature)
    {
        var start = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        var next = src.IndexOf("\n    public static ", start + signature.Length, StringComparison.Ordinal);
        return next < 0 ? src[start..] : src[start..next];
    }

    [Theory]
    [InlineData("StayHandlers.cs", "public static async Task<IResult> DeleteMine(", "SoftDeleteStayAsync")]
    [InlineData("AdminStayHandlers.cs", "public static async Task<IResult> DeleteStay(", "SoftDeleteStayAsync")]
    [InlineData("TaxiHandlers.cs", "public static async Task<IResult> DeleteMine(", "SoftDeleteTaxiAsync")]
    [InlineData("AdminTaxiHandlers.cs", "public static async Task<IResult> DeleteTaxi(", "SoftDeleteTaxiAsync")]
    public void Owner_and_admin_delete_use_the_one_removal(string file, string signature, string removal)
    {
        var body = Method(Src("RentNearBy.Api", "Handlers", file), signature);
        Assert.Contains("ListingRemoval." + removal, body);
        Assert.DoesNotContain("DeleteRoomPhotosAsync", body);
        Assert.DoesNotContain("BeginTransactionAsync", body);
    }

    [Theory]
    [InlineData("SoftDeleteStayAsync")]
    [InlineData("SoftDeleteTaxiAsync")]
    public void Removal_order_is_closure_delete_purge_commit_then_photos(string method)
    {
        var body = Method(Src("RentNearBy.Api", "Handlers", "Owner", "ListingRemoval.cs"), "public static async Task<ListingRemovalResult> " + method);
        int At(string s) => body.IndexOf(s, StringComparison.Ordinal);
        Assert.True(At("tripClosure.OnListingsRemovedAsync") < At("IsDeleted = true"));
        Assert.True(At("IsDeleted = true") < At("ownerPurge.PurgeAsync"));
        Assert.True(At("ownerPurge.PurgeAsync") < At("CommitTransactionAsync"));
        Assert.True(At("CommitTransactionAsync") < At("DeleteListingPhotosBestEffortAsync"));
    }

    [Fact]
    public void Stay_removal_drops_the_pending_edit_and_closes_jobs_inside_the_transaction()
    {
        var body = Method(Src("RentNearBy.Api", "Handlers", "Owner", "ListingRemoval.cs"), "public static async Task<ListingRemovalResult> SoftDeleteStayAsync");
        Assert.Contains("stay.PendingEditJson = null;", body);
        Assert.True(body.IndexOf("CloseActiveForStayAsync", StringComparison.Ordinal) < body.IndexOf("CommitTransactionAsync", StringComparison.Ordinal));
    }

    [Fact]
    public void Taxi_delete_count_includes_confirmed_trip_leads()
        => Assert.Contains("CountConfirmedLeadsAsync(db, taxi.Id, today)", Src("RentNearBy.Api", "Handlers", "Owner", "ListingRemoval.cs"));

    // FB-51: the hub warning (activeBookingCount) and the delete answer read the same lead count.
    [Fact]
    public void Taxi_hub_warning_counts_the_same_confirmed_trip_leads()
    {
        var hub = Src("RentNearBy.Infrastructure", "Services", "Owner", "Be3", "TaxiHubReader.cs");
        Assert.Contains("Apply(dto, taxi, limits, bookings + leads);", hub);
        Assert.Contains("TaxiHubReader.ConfirmedLeadsAsync(db, taxiId, today)", Src("RentNearBy.Api", "Handlers", "Owner", "ListingRemoval.cs"));
    }

    [Fact]
    public void Takedown_and_approval_drop_a_waiting_live_edit()
    {
        var src = Src("RentNearBy.Api", "Handlers", "AdminStayHandlers.cs");
        var takedown = src[src.IndexOf("private static async Task<IResult> TakeDownCoreAsync", StringComparison.Ordinal)..];
        takedown = takedown[..takedown.IndexOf("SaveChangesAsync", StringComparison.Ordinal)];
        Assert.Contains("stay.PendingEditJson = null;", takedown);
        Assert.Contains("stay.PendingEditJson = null;", Method(src, "public static async Task<IResult> ApproveStay("));
    }
}
