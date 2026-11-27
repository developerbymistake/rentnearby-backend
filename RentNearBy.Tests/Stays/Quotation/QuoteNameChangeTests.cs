using RentNearBy.Core.DTOs.Owner;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Services.Owner.Be3;

namespace RentNearBy.Tests.Stays.Quotation;

// Q5 / S10 / ARCHITECTURE 3.7: the one shared pending-edit writer (edit screen and quotation name change), the optional note and
// the admin row's note and mixed-script flag. Old JSON without a note still reads.
public class QuoteNameChangeTests
{
    private static StayListing Stay(string name = "Lake View Stay") => new()
    {
        Id = Guid.NewGuid(), UserId = Guid.NewGuid(), Name = name, Status = StayStatuses.Approved,
        RegistrationTypeId = Guid.NewGuid(), RegistrationTypeName = "Homestay", RegistrationNumber = "UK-1", Address = "Old road",
    };

    [Fact]
    public void Name_change_creates_a_pending_edit_with_only_the_name_and_the_note()
    {
        var stay = Stay();
        var now = DateTime.UtcNow;
        var outcome = StayPendingEditWriter.Apply(stay, StayPendingEditWriter.Basis(stay) with { Name = "Lake View Resort" }, now, "New board");
        Assert.Equal(StayReviewFields.SaveOutcome.NewPending, outcome);
        Assert.Equal("Lake View Stay", stay.Name);
        var pending = StayPendingEditJson.Read(stay.PendingEditJson)!;
        Assert.Equal(new[] { StayReviewFields.Name }, pending.Fields);
        Assert.Equal(("Lake View Resort", "New board"), (pending.Asked.Name, pending.Note));
        Assert.Equal(now, stay.PendingEditAt);
    }

    [Fact]
    public void Basis_keeps_the_other_waiting_fields()
    {
        var stay = Stay();
        StayPendingEditWriter.Apply(stay, StayPendingEditWriter.Basis(stay) with { Address = "New road" }, DateTime.UtcNow);
        StayPendingEditWriter.Apply(stay, StayPendingEditWriter.Basis(stay) with { Name = "Lake View Resort" }, DateTime.UtcNow, "Board");
        var pending = StayPendingEditJson.Read(stay.PendingEditJson)!;
        Assert.Equal(new[] { StayReviewFields.Name, StayReviewFields.Address }, pending.Fields);
        Assert.Equal("New road", pending.Asked.Address);
    }

    [Fact]
    public void Asking_the_live_name_again_clears_the_change()
    {
        var stay = Stay();
        StayPendingEditWriter.Apply(stay, StayPendingEditWriter.Basis(stay) with { Name = "Lake View Resort" }, DateTime.UtcNow);
        Assert.Equal(StayReviewFields.SaveOutcome.ClearPending,
            StayPendingEditWriter.Apply(stay, StayPendingEditWriter.Live(stay), DateTime.UtcNow));
        Assert.Null(stay.PendingEditJson);
    }

    [Fact]
    public void Same_change_with_a_new_note_is_written_again_and_without_a_note_it_is_unchanged()
    {
        var stay = Stay();
        var asked = StayPendingEditWriter.Basis(stay) with { Name = "Lake View Resort" };
        StayPendingEditWriter.Apply(stay, asked, DateTime.UtcNow, "First");
        Assert.Equal(StayReviewFields.SaveOutcome.Unchanged, StayPendingEditWriter.Apply(stay, asked, DateTime.UtcNow));
        Assert.Equal(StayReviewFields.SaveOutcome.NewPending, StayPendingEditWriter.Apply(stay, asked, DateTime.UtcNow, "Second"));
        Assert.Equal("Second", StayPendingEditJson.Read(stay.PendingEditJson)!.Note);
    }

    [Fact]
    public void Json_written_before_the_note_existed_still_reads()
    {
        var stay = Stay();
        var asked = StayPendingEditWriter.Basis(stay) with { Name = "X Stay" };
        var json = System.Text.Json.JsonSerializer.Serialize(new { fields = new[] { "name" }, asked },
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        var read = StayPendingEditJson.Read(json)!;
        Assert.Null(read.Note);
        Assert.Equal("X Stay", read.Asked.Name);
    }

    [Fact]
    public void Admin_row_carries_note_and_the_mixed_script_flag_with_defaults_for_old_callers()
    {
        var row = new AdminStayPendingEditRowDto(Guid.NewGuid(), "A", "B", DateTime.UtcNow, [], null, new StayPinDto(null, null), new StayPinDto(null, null));
        Assert.Null(row.Note);
        Assert.False(row.NameMixedScript);
        var src = File.ReadAllText(Path.Combine(RentNearBy.Tests.Trips.CompletionCounterGuardTests.Root(),
            "RentNearBy.Infrastructure", "Services", "Owner", "Be3", "StayPendingEditService.cs"));
        Assert.Contains("pending.Note, pending.Fields.Contains(StayReviewFields.Name) && StayNameScript.IsMixed(pending.Asked.Name)", src);
    }

    [Fact]
    public void Edit_screen_save_uses_the_shared_writer()
    {
        var src = File.ReadAllText(Path.Combine(RentNearBy.Tests.Trips.CompletionCounterGuardTests.Root(), "RentNearBy.Api", "Handlers", "StayHandlers.cs"));
        Assert.Contains("StayPendingEditWriter.Apply(stay, asked, now)", src);
        Assert.DoesNotContain("new StayPendingEdit(", src);
    }
}
