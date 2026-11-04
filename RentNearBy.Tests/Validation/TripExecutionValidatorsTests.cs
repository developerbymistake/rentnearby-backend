using System.Text.Json;
using FluentValidation;
using FluentValidation.TestHelper;
using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Tests.Validation;

public class TripExecutionValidatorsTests
{
    private static T Req<T>(Dictionary<string, object?> body, params (string Key, object? Value)[] overrides)
    {
        var copy = new Dictionary<string, object?>(body);
        foreach (var (k, v) in overrides) copy[k] = v;
        return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(copy, TripJson.Options), TripJson.Options)!;
    }

    private static TestValidationResult<T> Run<T>(IValidator<T> validator, T request) => validator.TestValidate(request);

    private static void Fails<T>(IValidator<T> validator, T request, string property)
    {
        var result = Run(validator, request);
        Assert.Contains(result.Errors, e => e.PropertyName == property || e.PropertyName.StartsWith(property + "[") || e.PropertyName.StartsWith(property + "."));
    }

    private static void Passes<T>(IValidator<T> validator, T request)
    {
        var result = Run(validator, request);
        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => $"{e.PropertyName}: {e.ErrorMessage}")));
    }

    // CreateTripEnquiryRequest
    private static readonly CreateTripEnquiryRequestValidator Create = new();

    private static Dictionary<string, object?> CreateBody() => new()
    {
        ["kind"] = "Stay", ["day"] = 1, ["listingIds"] = new[] { Guid.NewGuid() }, ["allNights"] = false,
    };

    [Fact]
    public void Create_valid_passes() => Passes(Create, Req<CreateTripEnquiryRequest>(CreateBody()));

    [Fact]
    public void Create_stay_kind_passes() => Passes(Create, Req<CreateTripEnquiryRequest>(CreateBody(), ("kind", "Stay")));

    [Theory]
    [InlineData("")]
    [InlineData("Room")]
    public void Create_bad_kind_fails(string kind) => Fails(Create, Req<CreateTripEnquiryRequest>(CreateBody(), ("kind", kind)), "Kind");

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(-1)]
    public void Create_day_out_of_range_fails(int day) => Fails(Create, Req<CreateTripEnquiryRequest>(CreateBody(), ("day", day)), "Day");

    // Every day has a night, so day 14 of a 14-day trip is a valid night.
    [Theory]
    [InlineData(1)]
    [InlineData(14)]
    public void Create_day_edges_pass(int day) => Passes(Create, Req<CreateTripEnquiryRequest>(CreateBody(), ("day", day)));

    [Fact]
    public void Create_no_listings_fails() => Fails(Create, Req<CreateTripEnquiryRequest>(CreateBody(), ("listingIds", Array.Empty<Guid>())), "ListingIds");

    [Fact]
    public void Create_four_listings_fails() =>
        Fails(Create, Req<CreateTripEnquiryRequest>(CreateBody(), ("listingIds", Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray())), "ListingIds");

    [Fact]
    public void Create_three_listings_pass() =>
        Passes(Create, Req<CreateTripEnquiryRequest>(CreateBody(), ("listingIds", Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray())));

    [Fact]
    public void Create_duplicate_listings_fail()
    {
        var id = Guid.NewGuid();
        Fails(Create, Req<CreateTripEnquiryRequest>(CreateBody(), ("listingIds", new[] { id, id })), "ListingIds");
    }

    [Fact]
    public void Create_empty_guid_fails() =>
        Fails(Create, Req<CreateTripEnquiryRequest>(CreateBody(), ("listingIds", new[] { Guid.Empty })), "ListingIds");

    // CreatePhoneBookingRequest
    private static readonly CreatePhoneBookingRequestValidator Phone = new();

    private static Dictionary<string, object?> PhoneBody() => new()
    {
        ["stayId"] = Guid.NewGuid(), ["day"] = 1, ["allNights"] = false, ["hostName"] = "Ramesh Bisht", ["hostPhone"] = "9876543210",
        ["roomName"] = "Deluxe", ["agreedTotal"] = 3000, ["advancePaid"] = false, ["advanceAmount"] = null, ["note"] = null,
        ["closeOthers"] = true, ["replaceEnquiryId"] = null,
    };

    [Fact]
    public void Phone_valid_passes() => Passes(Phone, Req<CreatePhoneBookingRequest>(PhoneBody()));

    [Fact]
    public void Phone_empty_stay_fails() => Fails(Phone, Req<CreatePhoneBookingRequest>(PhoneBody(), ("stayId", Guid.Empty)), "StayId");

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    public void Phone_day_out_of_range_fails(int day) => Fails(Phone, Req<CreatePhoneBookingRequest>(PhoneBody(), ("day", day)), "Day");

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Phone_host_name_required(string name) => Fails(Phone, Req<CreatePhoneBookingRequest>(PhoneBody(), ("hostName", name)), "HostName");

    [Fact]
    public void Phone_host_name_61_fails() => Fails(Phone, Req<CreatePhoneBookingRequest>(PhoneBody(), ("hostName", new string('a', 61))), "HostName");

    [Fact]
    public void Phone_host_name_60_passes() => Passes(Phone, Req<CreatePhoneBookingRequest>(PhoneBody(), ("hostName", new string('a', 60))));

    [Theory]
    [InlineData("12345")]
    [InlineData("5876543210")]
    [InlineData("98765432101")]
    [InlineData("98765abcde")]
    [InlineData("")]
    public void Phone_bad_host_phone_fails(string phone)
    {
        var result = Run(Phone, Req<CreatePhoneBookingRequest>(PhoneBody(), ("hostPhone", phone)));
        Assert.Contains(result.Errors, e => e.PropertyName == "HostPhone");
    }

    [Fact]
    public void Phone_bad_host_phone_message()
    {
        var result = Run(Phone, Req<CreatePhoneBookingRequest>(PhoneBody(), ("hostPhone", "12345")));
        result.ShouldHaveValidationErrorFor("HostPhone").WithErrorMessage("Enter a 10 digit phone");
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Phone_room_required(string? room) => Fails(Phone, Req<CreatePhoneBookingRequest>(PhoneBody(), ("roomName", room)), "RoomName");

    [Fact]
    public void Phone_room_41_fails() => Fails(Phone, Req<CreatePhoneBookingRequest>(PhoneBody(), ("roomName", new string('a', 41))), "RoomName");

    [Theory]
    [InlineData(0)]
    [InlineData(9_900_001)]
    [InlineData(-5)]
    public void Phone_total_out_of_range_fails(int total) => Fails(Phone, Req<CreatePhoneBookingRequest>(PhoneBody(), ("agreedTotal", total)), "AgreedTotal");

    [Theory]
    [InlineData(1)]
    [InlineData(9_900_000)]
    public void Phone_total_edges_pass(int total) => Passes(Phone, Req<CreatePhoneBookingRequest>(PhoneBody(), ("agreedTotal", total)));

    [Fact]
    public void Phone_advance_above_total_fails() =>
        Fails(Phone, Req<CreatePhoneBookingRequest>(PhoneBody(), ("advancePaid", true), ("advanceAmount", 3001)), "AdvanceAmount");

    [Fact]
    public void Phone_advance_paid_needs_an_amount()
    {
        Fails(Phone, Req<CreatePhoneBookingRequest>(PhoneBody(), ("advancePaid", true), ("advanceAmount", null)), "AdvanceAmount");
        Fails(Phone, Req<CreatePhoneBookingRequest>(PhoneBody(), ("advancePaid", true), ("advanceAmount", 0)), "AdvanceAmount");
    }

    [Fact]
    public void Phone_advance_paid_with_amount_passes() =>
        Passes(Phone, Req<CreatePhoneBookingRequest>(PhoneBody(), ("advancePaid", true), ("advanceAmount", 500)));

    [Fact]
    public void Phone_negative_advance_fails() =>
        Fails(Phone, Req<CreatePhoneBookingRequest>(PhoneBody(), ("advanceAmount", -1)), "AdvanceAmount");

    [Fact]
    public void Phone_note_301_fails() => Fails(Phone, Req<CreatePhoneBookingRequest>(PhoneBody(), ("note", new string('a', 301))), "Note");

    [Fact]
    public void Phone_note_300_passes() => Passes(Phone, Req<CreatePhoneBookingRequest>(PhoneBody(), ("note", new string('a', 300))));

    // EditPhoneBookingRequest
    private static readonly EditPhoneBookingRequestValidator Edit = new();

    private static Dictionary<string, object?> EditBody() => new()
    {
        ["allNights"] = false, ["hostName"] = "Ramesh Bisht", ["hostPhone"] = "9876543210", ["roomName"] = "Deluxe",
        ["agreedTotal"] = 3000, ["advancePaid"] = false, ["advanceAmount"] = null, ["note"] = null,
    };

    [Fact]
    public void Edit_valid_passes() => Passes(Edit, Req<EditPhoneBookingRequest>(EditBody()));

    [Fact]
    public void Edit_bad_phone_fails() => Fails(Edit, Req<EditPhoneBookingRequest>(EditBody(), ("hostPhone", "123")), "HostPhone");

    [Fact]
    public void Edit_total_zero_fails() => Fails(Edit, Req<EditPhoneBookingRequest>(EditBody(), ("agreedTotal", 0)), "AgreedTotal");

    [Fact]
    public void Edit_advance_above_total_fails() =>
        Fails(Edit, Req<EditPhoneBookingRequest>(EditBody(), ("advancePaid", true), ("advanceAmount", 4000)), "AdvanceAmount");

    [Fact]
    public void Edit_long_host_name_fails() => Fails(Edit, Req<EditPhoneBookingRequest>(EditBody(), ("hostName", new string('a', 61))), "HostName");

    // CancelTripEnquiryRequest
    private static readonly CancelTripEnquiryRequestValidator Cancel = new();

    [Fact]
    public void Cancel_null_reason_passes() => Passes(Cancel, Req<CancelTripEnquiryRequest>(new() { ["reason"] = null }));

    [Fact]
    public void Cancel_300_passes() => Passes(Cancel, Req<CancelTripEnquiryRequest>(new() { ["reason"] = new string('a', 300) }));

    [Fact]
    public void Cancel_301_fails() => Fails(Cancel, Req<CancelTripEnquiryRequest>(new() { ["reason"] = new string('a', 301) }), "Reason");

    // AcceptTripOfferRequest
    private static readonly AcceptTripOfferRequestValidator Accept = new();

    [Fact]
    public void Accept_defaults_pass() => Passes(Accept, Req<AcceptTripOfferRequest>(new()));

    [Fact]
    public void Accept_replace_id_passes() => Passes(Accept, Req<AcceptTripOfferRequest>(new() { ["replaceEnquiryId"] = Guid.NewGuid() }));

    [Fact]
    public void Accept_empty_replace_id_fails() =>
        Fails(Accept, Req<AcceptTripOfferRequest>(new() { ["replaceEnquiryId"] = Guid.Empty }), "ReplaceEnquiryId");

    [Fact]
    public void Accept_close_others_defaults_true() => Assert.True(Req<AcceptTripOfferRequest>(new()).CloseOthers);

    // SendTripOfferRequest
    private static readonly SendTripOfferRequestValidator Offer = new();

    private static object Line(string? room = "Deluxe", int units = 1, int guests = 4, int price = 1900) =>
        new { roomName = room, units, guests, pricePerNight = price };

    private static Dictionary<string, object?> OfferBody() => new()
    {
        ["checkIn"] = "2026-11-01", ["checkOut"] = "2026-11-02", ["lines"] = new[] { Line() }, ["advanceAmount"] = 500,
        ["balanceNote"] = "at check-in", ["cancellationText"] = "Free till 24 h before", ["gstInvoice"] = false,
        ["inclusions"] = "Breakfast", ["needsEchoed"] = new[] { "no_stairs" },
    };

    [Fact]
    public void Offer_valid_passes() => Passes(Offer, Req<SendTripOfferRequest>(OfferBody()));

    [Fact]
    public void Offer_check_out_not_after_check_in_fails()
    {
        Fails(Offer, Req<SendTripOfferRequest>(OfferBody(), ("checkOut", "2026-11-01")), "CheckOut");
        Fails(Offer, Req<SendTripOfferRequest>(OfferBody(), ("checkOut", "2026-10-30")), "CheckOut");
    }

    [Fact]
    public void Offer_14_nights_pass_15_fail()
    {
        Passes(Offer, Req<SendTripOfferRequest>(OfferBody(), ("checkOut", "2026-11-15")));
        var result = Run(Offer, Req<SendTripOfferRequest>(OfferBody(), ("checkOut", "2026-11-16")));
        result.ShouldHaveValidationErrorFor("CheckOut").WithErrorMessage("Nights must be at most 14");
    }

    [Fact]
    public void Offer_no_lines_fail() => Fails(Offer, Req<SendTripOfferRequest>(OfferBody(), ("lines", Array.Empty<object>())), "Lines");

    [Fact]
    public void Offer_five_lines_pass_six_fail()
    {
        Passes(Offer, Req<SendTripOfferRequest>(OfferBody(), ("lines", Enumerable.Range(0, 5).Select(_ => Line()).ToArray())));
        Fails(Offer, Req<SendTripOfferRequest>(OfferBody(), ("lines", Enumerable.Range(0, 6).Select(_ => Line()).ToArray())), "Lines");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void Offer_units_out_of_range_fail(int units) =>
        Fails(Offer, Req<SendTripOfferRequest>(OfferBody(), ("lines", new[] { Line(units: units) })), "Lines");

    [Theory]
    [InlineData(0)]
    [InlineData(41)]
    public void Offer_guests_out_of_range_fail(int guests) =>
        Fails(Offer, Req<SendTripOfferRequest>(OfferBody(), ("lines", new[] { Line(guests: guests) })), "Lines");

    [Theory]
    [InlineData(0)]
    [InlineData(9_900_001)]
    public void Offer_price_out_of_range_fail(int price) =>
        Fails(Offer, Req<SendTripOfferRequest>(OfferBody(), ("lines", new[] { Line(price: price) })), "Lines");

    // The per-night cap equals the stay listing cap.
    [Theory]
    [InlineData(200_001)]
    [InlineData(9_900_000)]
    public void Offer_price_edge_passes(int price) =>
        Passes(Offer, Req<SendTripOfferRequest>(OfferBody(), ("lines", new[] { Line(price: price) }), ("advanceAmount", null)));

    [Fact]
    public void Offer_room_name_41_fails() =>
        Fails(Offer, Req<SendTripOfferRequest>(OfferBody(), ("lines", new[] { Line(room: new string('a', 41)) })), "Lines");

    [Fact]
    public void Offer_room_name_null_passes() =>
        Passes(Offer, Req<SendTripOfferRequest>(OfferBody(), ("lines", new[] { Line(room: null) })));

    [Fact]
    public void Offer_negative_advance_fails() => Fails(Offer, Req<SendTripOfferRequest>(OfferBody(), ("advanceAmount", -1)), "AdvanceAmount");

    [Fact]
    public void Offer_balance_note_121_fails() => Fails(Offer, Req<SendTripOfferRequest>(OfferBody(), ("balanceNote", new string('a', 121))), "BalanceNote");

    [Fact]
    public void Offer_cancellation_301_fails() =>
        Fails(Offer, Req<SendTripOfferRequest>(OfferBody(), ("cancellationText", new string('a', 301))), "CancellationText");

    [Fact]
    public void Offer_inclusions_201_fails() => Fails(Offer, Req<SendTripOfferRequest>(OfferBody(), ("inclusions", new string('a', 201))), "Inclusions");

    [Fact]
    public void Offer_unknown_need_fails() =>
        Fails(Offer, Req<SendTripOfferRequest>(OfferBody(), ("needsEchoed", new[] { "no_stairs", "bogus" })), "NeedsEchoed");

    [Fact]
    public void Offer_all_known_needs_pass() =>
        Passes(Offer, Req<SendTripOfferRequest>(OfferBody(), ("needsEchoed", TripNeeds.All)));

    // TripOwnerListQuery
    private static readonly TripOwnerListQueryValidator OwnerList = new();

    private static Dictionary<string, object?> ListBody() => new() { ["status"] = "new", ["page"] = 1, ["pageSize"] = 20 };

    [Theory]
    [InlineData("new")]
    [InlineData("offered")]
    [InlineData("confirmed")]
    [InlineData("closed")]
    public void OwnerList_known_buckets_pass(string status) => Passes(OwnerList, Req<TripOwnerListQuery>(ListBody(), ("status", status)));

    [Fact]
    public void OwnerList_unknown_bucket_fails() => Fails(OwnerList, Req<TripOwnerListQuery>(ListBody(), ("status", "all")), "Status");

    [Fact]
    public void OwnerList_page_zero_fails() => Fails(OwnerList, Req<TripOwnerListQuery>(ListBody(), ("page", 0)), "Page");

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public void OwnerList_page_size_out_of_range_fails(int size) => Fails(OwnerList, Req<TripOwnerListQuery>(ListBody(), ("pageSize", size)), "PageSize");

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    public void OwnerList_page_size_edges_pass(int size) => Passes(OwnerList, Req<TripOwnerListQuery>(ListBody(), ("pageSize", size)));

    // CreateTripEnquiryRequest, taxi
    private static Dictionary<string, object?> TaxiLeadBody() => new()
    {
        ["kind"] = "Taxi", ["days"] = new[] { 1, 2 }, ["listingIds"] = new[] { Guid.NewGuid() }, ["pickupTime"] = "08:30", ["pickupPlace"] = "Mallital",
    };

    [Fact]
    public void Taxi_lead_valid_passes() => Passes(Create, Req<CreateTripEnquiryRequest>(TaxiLeadBody()));

    [Fact]
    public void Taxi_lead_does_not_need_a_stay_day() =>
        Passes(Create, Req<CreateTripEnquiryRequest>(TaxiLeadBody(), ("day", 0)));

    [Fact]
    public void Taxi_lead_without_days_fails() =>
        Fails(Create, Req<CreateTripEnquiryRequest>(TaxiLeadBody(), ("days", null)), "Days");

    [Fact]
    public void Taxi_lead_empty_days_fails() =>
        Fails(Create, Req<CreateTripEnquiryRequest>(TaxiLeadBody(), ("days", Array.Empty<int>())), "Days");

    [Fact]
    public void Taxi_lead_fifteen_days_fails() =>
        Fails(Create, Req<CreateTripEnquiryRequest>(TaxiLeadBody(), ("days", Enumerable.Range(1, 15).ToArray())), "Days");

    // A whole-trip lead covers every day of a 14-day trip.
    [Fact]
    public void Taxi_lead_fourteen_days_pass() =>
        Passes(Create, Req<CreateTripEnquiryRequest>(TaxiLeadBody(), ("days", Enumerable.Range(1, 14).ToArray())));

    [Fact]
    public void Taxi_lead_duplicate_days_fail() =>
        Fails(Create, Req<CreateTripEnquiryRequest>(TaxiLeadBody(), ("days", new[] { 2, 2 })), "Days");

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    public void Taxi_lead_day_out_of_range_fails(int day) =>
        Fails(Create, Req<CreateTripEnquiryRequest>(TaxiLeadBody(), ("days", new[] { day })), "Days");

    [Fact]
    public void Taxi_lead_day_14_passes() =>
        Passes(Create, Req<CreateTripEnquiryRequest>(TaxiLeadBody(), ("days", new[] { 14 })));

    [Theory]
    [InlineData("8:30")]
    [InlineData("24:00")]
    [InlineData("08:60")]
    [InlineData("0830")]
    [InlineData("")]
    public void Taxi_lead_bad_pickup_time_fails(string time) =>
        Fails(Create, Req<CreateTripEnquiryRequest>(TaxiLeadBody(), ("pickupTime", time)), "PickupTime");

    [Theory]
    [InlineData("00:00")]
    [InlineData("08:30")]
    [InlineData("23:59")]
    public void Taxi_lead_pickup_time_edges_pass(string time) =>
        Passes(Create, Req<CreateTripEnquiryRequest>(TaxiLeadBody(), ("pickupTime", time)));

    [Fact]
    public void Taxi_lead_pickup_is_optional() =>
        Passes(Create, Req<CreateTripEnquiryRequest>(TaxiLeadBody(), ("pickupTime", null), ("pickupPlace", null)));

    [Fact]
    public void Taxi_lead_pickup_place_length() =>
        Fails(Create, Req<CreateTripEnquiryRequest>(TaxiLeadBody(), ("pickupPlace", new string('a', 81))), "PickupPlace");

    [Fact]
    public void Taxi_lead_pickup_place_edge_passes() =>
        Passes(Create, Req<CreateTripEnquiryRequest>(TaxiLeadBody(), ("pickupPlace", new string('a', 80))));

    [Fact]
    public void Taxi_lead_four_drivers_fail() =>
        Fails(Create, Req<CreateTripEnquiryRequest>(TaxiLeadBody(), ("listingIds", Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray())), "ListingIds");

    // CreateTaxiBookingRequest
    private static readonly CreateTaxiBookingRequestValidator TaxiBooking = new();

    private static Dictionary<string, object?> TaxiBookingBody() => new()
    {
        ["taxiId"] = Guid.NewGuid(), ["days"] = new[] { 1, 2 }, ["agreedFare"] = 3800, ["tollExtra"] = true, ["pickupTime"] = "08:30",
        ["pickupPlace"] = "Mallital", ["note"] = null, ["closeOthers"] = true, ["replaceEnquiryId"] = null,
    };

    [Fact]
    public void TaxiBooking_valid_passes() => Passes(TaxiBooking, Req<CreateTaxiBookingRequest>(TaxiBookingBody()));

    [Fact]
    public void TaxiBooking_empty_taxi_fails() =>
        Fails(TaxiBooking, Req<CreateTaxiBookingRequest>(TaxiBookingBody(), ("taxiId", Guid.Empty)), "TaxiId");

    [Fact]
    public void TaxiBooking_no_days_fails() =>
        Fails(TaxiBooking, Req<CreateTaxiBookingRequest>(TaxiBookingBody(), ("days", Array.Empty<int>())), "Days");

    [Fact]
    public void TaxiBooking_duplicate_days_fail() =>
        Fails(TaxiBooking, Req<CreateTaxiBookingRequest>(TaxiBookingBody(), ("days", new[] { 1, 1 })), "Days");

    [Fact]
    public void TaxiBooking_fourteen_days_pass() =>
        Passes(TaxiBooking, Req<CreateTaxiBookingRequest>(TaxiBookingBody(), ("days", Enumerable.Range(1, 14).ToArray())));

    [Fact]
    public void TaxiBooking_fifteen_days_fail() =>
        Fails(TaxiBooking, Req<CreateTaxiBookingRequest>(TaxiBookingBody(), ("days", Enumerable.Range(1, 15).ToArray())), "Days");

    [Theory]
    [InlineData(99)]
    [InlineData(0)]
    [InlineData(9_900_001)]
    public void TaxiBooking_fare_out_of_range_fails(int fare) =>
        Fails(TaxiBooking, Req<CreateTaxiBookingRequest>(TaxiBookingBody(), ("agreedFare", fare)), "AgreedFare");

    [Theory]
    [InlineData(100)]
    [InlineData(9_900_000)]
    public void TaxiBooking_fare_edges_pass(int fare) =>
        Passes(TaxiBooking, Req<CreateTaxiBookingRequest>(TaxiBookingBody(), ("agreedFare", fare)));

    [Theory]
    [InlineData("")]
    [InlineData("8:30")]
    [InlineData("25:00")]
    public void TaxiBooking_pickup_time_required_and_shaped(string time) =>
        Fails(TaxiBooking, Req<CreateTaxiBookingRequest>(TaxiBookingBody(), ("pickupTime", time)), "PickupTime");

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TaxiBooking_pickup_place_required(string place) =>
        Fails(TaxiBooking, Req<CreateTaxiBookingRequest>(TaxiBookingBody(), ("pickupPlace", place)), "PickupPlace");

    [Fact]
    public void TaxiBooking_note_length() =>
        Fails(TaxiBooking, Req<CreateTaxiBookingRequest>(TaxiBookingBody(), ("note", new string('a', 301))), "Note");

    [Fact]
    public void TaxiBooking_note_edge_passes() =>
        Passes(TaxiBooking, Req<CreateTaxiBookingRequest>(TaxiBookingBody(), ("note", new string('a', 300))));

    [Fact]
    public void TaxiBooking_empty_replace_id_fails() =>
        Fails(TaxiBooking, Req<CreateTaxiBookingRequest>(TaxiBookingBody(), ("replaceEnquiryId", Guid.Empty)), "ReplaceEnquiryId");

    // AskAdvanceRequest
    private static readonly AskAdvanceRequestValidator Ask = new();

    private static Dictionary<string, object?> AskBody() => new()
    {
        ["amount"] = 500, ["holdUntil"] = "2026-11-01T12:30:00Z", ["upiId"] = "name@bank", ["whatsAppNumber"] = "9876543210",
    };

    [Fact]
    public void Ask_valid_passes() => Passes(Ask, Req<AskAdvanceRequest>(AskBody()));

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(9_900_001)]
    public void Ask_amount_out_of_range_fails(int amount) =>
        Fails(Ask, Req<AskAdvanceRequest>(AskBody(), ("amount", amount)), "Amount");

    [Theory]
    [InlineData(1)]
    [InlineData(9_900_000)]
    public void Ask_amount_edges_pass(int amount) => Passes(Ask, Req<AskAdvanceRequest>(AskBody(), ("amount", amount)));

    [Theory]
    [InlineData("")]
    [InlineData("name")]
    [InlineData("@bank")]
    [InlineData("a@b")]
    [InlineData("na me@bank")]
    public void Ask_bad_upi_fails(string upi) => Fails(Ask, Req<AskAdvanceRequest>(AskBody(), ("upiId", upi)), "UpiId");

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("5876543210")]
    [InlineData("98765432101")]
    [InlineData("98765abcde")]
    public void Ask_bad_whatsapp_fails(string phone) =>
        Fails(Ask, Req<AskAdvanceRequest>(AskBody(), ("whatsAppNumber", phone)), "WhatsAppNumber");

    [Fact]
    public void Ask_hold_with_an_offset_passes() =>
        Passes(Ask, Req<AskAdvanceRequest>(AskBody(), ("holdUntil", "2026-11-01T18:00:00+05:30")));

    [Fact]
    public void Ask_hold_without_a_zone_fails() =>
        Fails(Ask, Req<AskAdvanceRequest>(AskBody(), ("holdUntil", "2026-11-01T12:30:00")), "HoldUntil");

    // DriverFareRequest
    private static readonly DriverFareRequestValidator DriverFare = new();

    [Theory]
    [InlineData(99)]
    [InlineData(0)]
    [InlineData(9_900_001)]
    public void DriverFare_out_of_range_fails(int fare) =>
        Fails(DriverFare, Req<DriverFareRequest>(new() { ["fare"] = fare, ["note"] = null }), "Fare");

    [Theory]
    [InlineData(100)]
    [InlineData(3800)]
    [InlineData(9_900_000)]
    public void DriverFare_in_range_passes(int fare) =>
        Passes(DriverFare, Req<DriverFareRequest>(new() { ["fare"] = fare, ["note"] = "Hill route" }));

    [Fact]
    public void DriverFare_note_length()
    {
        Fails(DriverFare, Req<DriverFareRequest>(new() { ["fare"] = 3800, ["note"] = new string('a', 301) }), "Note");
        Passes(DriverFare, Req<DriverFareRequest>(new() { ["fare"] = 3800, ["note"] = new string('a', 300) }));
    }

    // TripDriverListQuery
    private static readonly TripDriverListQueryValidator DriverList = new();

    private static Dictionary<string, object?> DriverListBody() => new() { ["status"] = "new", ["page"] = 1, ["pageSize"] = 20 };

    [Theory]
    [InlineData("new")]
    [InlineData("replied")]
    [InlineData("booked")]
    [InlineData("closed")]
    public void DriverList_known_buckets_pass(string status) =>
        Passes(DriverList, Req<TripDriverListQuery>(DriverListBody(), ("status", status)));

    [Theory]
    [InlineData("offered")]
    [InlineData("confirmed")]
    [InlineData("all")]
    [InlineData("")]
    public void DriverList_stay_buckets_fail(string status) =>
        Fails(DriverList, Req<TripDriverListQuery>(DriverListBody(), ("status", status)), "Status");

    [Fact]
    public void DriverList_page_zero_fails() => Fails(DriverList, Req<TripDriverListQuery>(DriverListBody(), ("page", 0)), "Page");

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public void DriverList_page_size_out_of_range_fails(int size) =>
        Fails(DriverList, Req<TripDriverListQuery>(DriverListBody(), ("pageSize", size)), "PageSize");

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    public void DriverList_page_size_edges_pass(int size) =>
        Passes(DriverList, Req<TripDriverListQuery>(DriverListBody(), ("pageSize", size)));

    // UpdateStayAdvanceDetailsRequest
    private static readonly UpdateStayAdvanceDetailsRequestValidator StayAdvance = new();

    [Fact]
    public void StayAdvance_nulls_keep_and_pass() =>
        Passes(StayAdvance, Req<UpdateStayAdvanceDetailsRequest>(new() { ["upiId"] = null, ["whatsAppNumber"] = null }));

    [Fact]
    public void StayAdvance_empty_strings_clear_and_pass() =>
        Passes(StayAdvance, Req<UpdateStayAdvanceDetailsRequest>(new() { ["upiId"] = "", ["whatsAppNumber"] = "" }));

    [Fact]
    public void StayAdvance_valid_values_pass() =>
        Passes(StayAdvance, Req<UpdateStayAdvanceDetailsRequest>(new() { ["upiId"] = "name@bank", ["whatsAppNumber"] = "9876543210" }));

    [Theory]
    [InlineData("name")]
    [InlineData("@bank")]
    [InlineData("name@bank.com")]
    public void StayAdvance_bad_upi_fails(string upi) =>
        Fails(StayAdvance, Req<UpdateStayAdvanceDetailsRequest>(new() { ["upiId"] = upi, ["whatsAppNumber"] = null }), "UpiId");

    [Theory]
    [InlineData("12345")]
    [InlineData("5876543210")]
    public void StayAdvance_bad_phone_fails(string phone) =>
        Fails(StayAdvance, Req<UpdateStayAdvanceDetailsRequest>(new() { ["upiId"] = null, ["whatsAppNumber"] = phone }), "WhatsAppNumber");

    // AdminCloseTripEnquiryRequest
    private static readonly AdminCloseTripEnquiryRequestValidator AdminClose = new();

    [Fact]
    public void AdminClose_nine_chars_fails() =>
        Fails(AdminClose, Req<AdminCloseTripEnquiryRequest>(new() { ["note"] = new string('a', 9) }), "Note");

    [Fact]
    public void AdminClose_ten_chars_passes() =>
        Passes(AdminClose, Req<AdminCloseTripEnquiryRequest>(new() { ["note"] = new string('a', 10) }));

    [Fact]
    public void AdminClose_three_hundred_passes_and_301_fails()
    {
        Passes(AdminClose, Req<AdminCloseTripEnquiryRequest>(new() { ["note"] = new string('a', 300) }));
        Fails(AdminClose, Req<AdminCloseTripEnquiryRequest>(new() { ["note"] = new string('a', 301) }), "Note");
    }

    [Fact]
    public void AdminClose_missing_or_blank_note_fails()
    {
        Fails(AdminClose, Req<AdminCloseTripEnquiryRequest>(new() { ["note"] = null }), "Note");
        Fails(AdminClose, Req<AdminCloseTripEnquiryRequest>(new() { ["note"] = "          " }), "Note");
    }

    // Phase 2: counter, change request, revised offer, ask again, counterOfferId
    private static readonly CounterRequestValidator Counter = new();
    private static readonly ChangeRequestRequestValidator Change = new();
    private static readonly RevisedOfferRequestValidator Revised = new();
    private static readonly AskAgainRequestValidator AskAgain = new();

    [Theory]
    [InlineData(100)]
    [InlineData(3600)]
    [InlineData(200001)]
    [InlineData(9900000)]
    public void Counter_price_inside_range_passes(int price) =>
        Passes(Counter, Req<CounterRequest>(new() { ["pricePerNight"] = price }));

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    [InlineData(9900001)]
    [InlineData(-5)]
    public void Counter_price_outside_range_fails(int price)
    {
        var result = Run(Counter, Req<CounterRequest>(new() { ["pricePerNight"] = price }));
        Assert.Contains(result.Errors, e => e.PropertyName == "PricePerNight");
        Assert.Contains(result.Errors, e => e.ErrorMessage == "Price per night must be between Rs 100 and Rs 99,00,000");
    }

    [Fact]
    public void Counter_note_limit_is_300()
    {
        Passes(Counter, Req<CounterRequest>(new() { ["pricePerNight"] = 3600, ["note"] = new string('a', 300) }));
        Passes(Counter, Req<CounterRequest>(new() { ["pricePerNight"] = 3600, ["note"] = null }));
        Fails(Counter, Req<CounterRequest>(new() { ["pricePerNight"] = 3600, ["note"] = new string('a', 301) }), "Note");
    }

    private static Dictionary<string, object?> ChangeBody() => new()
    {
        ["checkIn"] = "2026-11-08", ["checkOut"] = "2026-11-09", ["adults"] = 2, ["kids"] = 0, ["rooms"] = 1, ["note"] = "Two guests dropped out",
    };

    [Fact]
    public void Change_valid_passes() => Passes(Change, Req<ChangeRequestRequest>(ChangeBody()));

    [Fact]
    public void Change_equal_or_reversed_dates_fail()
    {
        Fails(Change, Req<ChangeRequestRequest>(ChangeBody(), ("checkOut", "2026-11-08")), "CheckOut");
        Fails(Change, Req<ChangeRequestRequest>(ChangeBody(), ("checkOut", "2026-11-07")), "CheckOut");
    }

    [Fact]
    public void Change_nights_limit_is_14()
    {
        Passes(Change, Req<ChangeRequestRequest>(ChangeBody(), ("checkOut", "2026-11-22")));
        Fails(Change, Req<ChangeRequestRequest>(ChangeBody(), ("checkOut", "2026-11-23")), "CheckOut");
    }

    [Fact]
    public void Day_and_nights_messages_say_14()
    {
        Run(Create, Req<CreateTripEnquiryRequest>(CreateBody(), ("day", 15)))
            .ShouldHaveValidationErrorFor("Day").WithErrorMessage("Day must be 1 to 14");
        Run(Change, Req<ChangeRequestRequest>(ChangeBody(), ("checkOut", "2026-11-23")))
            .ShouldHaveValidationErrorFor("CheckOut").WithErrorMessage("Nights must be at most 14");
    }

    [Fact]
    public void Revised_fourteen_nights_pass() =>
        Passes(Revised, Req<RevisedOfferRequest>(RevisedBody(), ("checkOut", "2026-11-15")));

    [Theory]
    [InlineData("adults", 0, false)]
    [InlineData("adults", 1, true)]
    [InlineData("adults", 30, true)]
    [InlineData("adults", 31, false)]
    [InlineData("kids", -1, false)]
    [InlineData("kids", 0, true)]
    [InlineData("kids", 20, true)]
    [InlineData("kids", 21, false)]
    [InlineData("rooms", 0, false)]
    [InlineData("rooms", 1, true)]
    [InlineData("rooms", 15, true)]
    [InlineData("rooms", 16, false)]
    public void Change_people_and_rooms_limits(string field, int value, bool ok)
    {
        var req = Req<ChangeRequestRequest>(ChangeBody(), (field, value));
        if (ok) Passes(Change, req);
        else Fails(Change, req, char.ToUpperInvariant(field[0]) + field[1..]);
    }

    [Fact]
    public void Change_note_limit_is_300()
    {
        Passes(Change, Req<ChangeRequestRequest>(ChangeBody(), ("note", new string('a', 300))));
        Fails(Change, Req<ChangeRequestRequest>(ChangeBody(), ("note", new string('a', 301))), "Note");
    }

    private static Dictionary<string, object?> RevisedBody() => new(OfferBody()) { ["reason"] = "Diwali week rate" };

    [Fact]
    public void Revised_valid_passes() => Passes(Revised, Req<RevisedOfferRequest>(RevisedBody()));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Revised_empty_reason_fails(string reason) =>
        Fails(Revised, Req<RevisedOfferRequest>(RevisedBody(), ("reason", reason)), "Reason");

    [Fact]
    public void Revised_reason_limit_is_300()
    {
        Passes(Revised, Req<RevisedOfferRequest>(RevisedBody(), ("reason", new string('a', 300))));
        Fails(Revised, Req<RevisedOfferRequest>(RevisedBody(), ("reason", new string('a', 301))), "Reason");
    }

    [Fact]
    public void Revised_keeps_the_offer_rules()
    {
        Fails(Revised, Req<RevisedOfferRequest>(RevisedBody(), ("checkOut", "2026-11-01")), "CheckOut");
        Fails(Revised, Req<RevisedOfferRequest>(RevisedBody(), ("lines", Array.Empty<object>())), "Lines");
        Fails(Revised, Req<RevisedOfferRequest>(RevisedBody(), ("lines", new[] { Line(price: 0) })), "Lines");
    }

    [Fact]
    public void Revised_change_request_id_must_not_be_empty()
    {
        Passes(Revised, Req<RevisedOfferRequest>(RevisedBody(), ("changeRequestId", Guid.NewGuid())));
        Passes(Revised, Req<RevisedOfferRequest>(RevisedBody(), ("changeRequestId", null)));
        Fails(Revised, Req<RevisedOfferRequest>(RevisedBody(), ("changeRequestId", Guid.Empty)), "ChangeRequestId");
    }

    [Fact]
    public void Offer_counter_offer_id_must_not_be_empty()
    {
        Passes(Offer, Req<SendTripOfferRequest>(OfferBody(), ("counterOfferId", Guid.NewGuid())));
        Passes(Offer, Req<SendTripOfferRequest>(OfferBody(), ("counterOfferId", null)));
        Fails(Offer, Req<SendTripOfferRequest>(OfferBody(), ("counterOfferId", Guid.Empty)), "CounterOfferId");
    }

    [Theory]
    [InlineData("Stay", 1, true)]
    [InlineData("Taxi", 14, true)]
    [InlineData("Stay", 0, false)]
    [InlineData("Taxi", 15, false)]
    [InlineData("Room", 1, false)]
    [InlineData("", 1, false)]
    public void AskAgain_kind_and_day(string kind, int day, bool ok)
    {
        var req = Req<AskAgainRequest>(new() { ["kind"] = kind, ["day"] = day });
        if (ok) Passes(AskAgain, req);
        else Assert.False(Run(AskAgain, req).IsValid);
    }
}
