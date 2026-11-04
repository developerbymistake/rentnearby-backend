using System.Security.Claims;
using System.Text.Json;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

// Trip-level lifecycle: problem, complete, mute, summary, and the owner's report of a traveller.
public static class TripLifecycleHandlers
{
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    private static readonly TimeSpan Day24 = TimeSpan.FromHours(24);
    private const int ProblemsPerDay = 10;
    private const int ReportSubmitMax = 5;
    private const int ReportDetailsMax = 500;

    private static IResult TripNotFound() => ErrorResponse(404, "Trip not found", TripErrorTypes.TripNotFound);

    private static IResult Concurrent()
        => ConflictResponse("This trip just changed.", TripEnquiryErrorTypes.ConcurrentUpdate);

    // The enquiry id a stay/taxi report may carry: only the caller's own enquiry for this listing, else ignored.
    public static async Task<Guid?> OwnTripEnquiryIdAsync(IUnitOfWork unitOfWork, Guid? tripEnquiryId, Guid listingId, Guid callerId)
    {
        if (tripEnquiryId is not { } id) return null;
        var e = await unitOfWork.TripEnquiries.GetByIdAsync(id);
        return e != null && e.ListingId == listingId && e.TravellerUserId == callerId ? id : null;
    }

    // ---------- POST /trips/{tripId}/problems ----------

    public static async Task<IResult> Problem(
        Guid tripId, TripProblemRequest request, IValidator<TripProblemRequest> validator, ClaimsPrincipal principal,
        IUnitOfWork unitOfWork, ApplicationDbContext db, ITripChatService chat, ITripNotifier notifier,
        IRateLimitService rateLimiter, ILoggerFactory loggers, HttpContext http)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var trip = await unitOfWork.Trips.GetByIdForOwnerAsync(tripId, userId);
        if (trip == null) return TripNotFound();

        var now = DateTime.UtcNow;
        if (!TripReminderRules.ProblemAllowed(trip, TripExecutionRules.TodayIst(now)))
            return ErrorResponse(422, "Problem can be reported during the trip.", TripEnquiryErrorTypes.ProblemOutsideTrip);

        if (await TripEnquiryHandlers.RateLimitedAsync(rateLimiter, http, $"trip:problem:{userId}", ProblemsPerDay, Day24) is { } limited)
            return limited;

        TripEnquiry? e = null;
        if (TripProblemKinds.MarksBooking(request.Kind) && request.EnquiryId is { } enquiryId)
        {
            e = await unitOfWork.TripEnquiries.GetByIdTrackedAsync(enquiryId);
            if (e == null || e.TripId != tripId || e.TravellerUserId != userId || e.Kind != TripProblemKinds.ExpectedKind(request.Kind))
                return BadRequestResponse("That booking does not match the problem");
        }

        var posts = new List<TripChatPost>();
        var pushes = new List<TripPendingPush>();
        var marked = false;
        TripEnquiry? kept = null, failed = null;
        var log = new TripProblemLog
        {
            Id = Guid.NewGuid(),
            TripId = tripId,
            TripEnquiryId = e?.Id,
            Day = (short)request.Day,
            Kind = request.Kind,
            Note = TripTextSafety.CleanOrNull(request.Note, TripProblemRequestValidator.NoteMax),
            Latitude = request.Latitude,
            Longitude = request.Longitude,
            CreatedAt = now,
        };

        await unitOfWork.BeginTransactionAsync();
        try
        {
            db.TripProblemLogs.Add(log);
            var tripStart = trip.StartDate ?? TripHandlers.ReadPlan(trip).Settings?.StartDate;
            if (e != null && tripStart is { } start && TripEnquiryStatuses.IsBooked(e.Status)
                && TripProblemSplit.Affects(e, start, request.Day))
            {
                // Only nights/days from the reported day on fail; the ones already used stay booked.
                (kept, failed) = TripProblemSplit.Plan(e, start, request.Day, now);
                marked = true;
                if (kept != null) db.TripEnquiries.Add(failed);
                log.TripEnquiryId = failed.Id;
                if (e.Kind == TripEnquiryKinds.Stay && e.ConversationId != null)
                {
                    if (kept == null)
                        await TripEnquiryOps.CloseOpenOfferAsync(db, chat, e, TripOfferStatuses.Superseded, TripCardStatuses.Superseded, posts, now);
                    await TripEnquiryOps.PostSystemAsync(db, chat, failed, userId, TripSystemLines.ProblemHostNoRoom, posts);
                }
            }
            await db.SaveChangesAsync();
            await unitOfWork.CommitTransactionAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            await unitOfWork.RollbackTransactionAsync();
            return Concurrent();
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }

        loggers.CreateLogger("TripLifecycle").LogInformation("trip.problem {Kind} {TripId}", request.Kind, tripId);
        if (marked && failed != null)
            pushes.Add(new TripPendingPush(failed, failed.OwnerUserId, TripNotifications.TripProblemReported));
        await TripEnquiryHandlers.AfterCommitAsync(chat, posts, pushes, unitOfWork, notifier, loggers);

        return CreatedResponse(new TripProblemResultDto
        {
            ProblemId = log.Id,
            Enquiry = e == null ? null : await TripEnquiryDtoBuilder.BuildOneAsync(unitOfWork, failed ?? e, userId, now),
            KeptEnquiry = kept == null ? null : await TripEnquiryDtoBuilder.BuildOneAsync(unitOfWork, kept, userId, now),
            Marked = marked,
        }, $"/api/v1/trips/{tripId}");
    }

    // ---------- POST /trips/{tripId}/complete ----------

    public static async Task<IResult> Complete(
        Guid tripId, ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db, ITripChatService chat,
        ILoggerFactory loggers)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();

        var trip = await unitOfWork.Trips.GetByIdForOwnerAsync(tripId, userId);
        if (trip == null) return TripNotFound();

        var now = DateTime.UtcNow;
        if (trip.Status != TripStatuses.Completed)
        {
            if (!TripReminderRules.CanComplete(trip, TripExecutionRules.TodayIst(now)))
                return ConflictResponse("The trip has not started yet.", TripEnquiryErrorTypes.TripNotEnded);

            trip.Status = TripStatuses.Completed;
            trip.CompletedAt = now;
            trip.UpdatedAt = now;
            try
            {
                await unitOfWork.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                return Concurrent();
            }
            loggers.CreateLogger("TripLifecycle").LogInformation("trip.completed {TripId} manual", tripId);
            await PostCompletedLinesAsync(unitOfWork, db, chat, loggers, trip, userId);
        }

        return OkResponse(await TripHandlers.BuildDtoAsync(unitOfWork, trip, userId, DateTime.UtcNow));
    }

    // Best effort: the hosts of confirmed stays see that the trip ended; a failure never undoes the completion.
    private static async Task PostCompletedLinesAsync(
        IUnitOfWork unitOfWork, ApplicationDbContext db, ITripChatService chat, ILoggerFactory loggers, Trip trip, Guid userId)
    {
        var posts = new List<TripChatPost>();
        try
        {
            var stays = await db.TripEnquiries.AsNoTracking()
                .Where(e => e.TripId == trip.Id && e.Kind == TripEnquiryKinds.Stay && e.ConversationId != null
                    && e.Status == TripEnquiryStatuses.ConfirmedByBoth)
                .ToListAsync();
            if (stays.Count == 0) return;

            await unitOfWork.BeginTransactionAsync();
            try
            {
                foreach (var e in stays)
                    await TripEnquiryOps.PostSystemAsync(db, chat, e, userId, TripSystemLines.TripCompleted, posts);
                await db.SaveChangesAsync();
                await unitOfWork.CommitTransactionAsync();
            }
            catch
            {
                await unitOfWork.RollbackTransactionAsync();
                posts.Clear();
                throw;
            }
            await chat.AfterCommitAsync(posts);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            loggers.CreateLogger("TripLifecycle").LogWarning(ex, "trip.completed.lines_failed {TripId}", trip.Id);
        }
    }

    // ---------- PUT /trips/{tripId}/mute ----------

    public static async Task<IResult> Mute(Guid tripId, MuteTripRequest request, ClaimsPrincipal principal, IUnitOfWork unitOfWork)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();

        var trip = await unitOfWork.Trips.GetByIdForOwnerAsync(tripId, userId);
        if (trip == null) return TripNotFound();

        var now = DateTime.UtcNow;
        var changed = request.Muted ? trip.MutedAt == null : trip.MutedAt != null;
        if (changed)
        {
            trip.MutedAt = request.Muted ? now : null;
            trip.UpdatedAt = now;
            try
            {
                await unitOfWork.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                return Concurrent();
            }
        }

        return OkResponse(await TripHandlers.BuildDtoAsync(unitOfWork, trip, userId, now));
    }

    // ---------- GET /trips/{tripId}/summary ----------

    public static async Task<IResult> Summary(Guid tripId, ClaimsPrincipal principal, IUnitOfWork unitOfWork)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();

        var trip = await unitOfWork.Trips.GetByIdForOwnerAsync(tripId, userId);
        if (trip == null) return TripNotFound();

        var now = DateTime.UtcNow;
        var raw = await unitOfWork.TripEnquiries.GetByTripAsync(trip.Id);
        var dtos = await TripEnquiryDtoBuilder.BuildManyAsync(unitOfWork, raw, userId, now);
        return OkResponse(TripSummaryRecordBuilder.Build(trip, TripHandlers.ReadPlan(trip), dtos, raw, now));
    }

    // ---------- POST /trips/enquiries/{id}/report-traveller ----------

    public static async Task<IResult> ReportTraveller(
        Guid id, ReportTravellerRequest request, IValidator<ReportTravellerRequest> validator, ClaimsPrincipal principal,
        IUnitOfWork unitOfWork, ApplicationDbContext db, IRabbitMqPublisher publisher, IRateLimitService rateLimiter,
        HttpContext http)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var callerId)) return UnauthorizedResponse();

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var e = await unitOfWork.TripEnquiries.GetByIdTrackedAsync(id);
        if (await TripEnquiryAuth.RequireOwnerAsync(e, callerId, db) is { } denied) return denied;
        if (await TripEnquiryHandlers.RateLimitedAsync(
                rateLimiter, http, $"trip:owner:{callerId}", TripExecutionRules.OwnerActionsPerHour, Hour) is { } limited)
            return limited;
        if (await TripEnquiryHandlers.RateLimitedAsync(rateLimiter, http, $"report:submit:{callerId}", ReportSubmitMax, Hour) is { } reportLimited)
            return reportLimited;

        if (e!.TravellerUserId is not { } travellerId)
            return ConflictResponse("This traveller left Bakhli.", TripEnquiryErrorTypes.TripEnquiryClosed);

        var reason = await unitOfWork.ReportReasons.GetByIdAsync(request.ReasonId);
        if (reason == null) return BadRequestResponse("Invalid reason");

        var reporter = await unitOfWork.Users.GetByIdAsync(callerId);
        var traveller = await unitOfWork.Users.GetByIdAsync(travellerId);

        var report = new ListingReport
        {
            Id = Guid.NewGuid(),
            ListingId = e.Id,
            ListingType = ListingReportTypes.TripEnquiry,
            ReporterUserId = callerId,
            ReporterName = reporter?.Name ?? "Unknown",
            ReporterMobile = reporter?.PhoneNumber ?? "",
            ReportedUserId = travellerId,
            ReportedName = traveller?.Name ?? "Unknown",
            ReportedMobile = traveller?.PhoneNumber ?? "",
            ReasonId = request.ReasonId,
            Details = TripTextSafety.Clean(request.Details, ReportDetailsMax),
            Status = "Pending",
            CreatedAt = DateTime.UtcNow,
            TripEnquiryId = e.Id,
        };

        await unitOfWork.ListingReports.AddAsync(report);
        try
        {
            await unitOfWork.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            return ConflictResponse("You have already reported this traveller");
        }

        var message = new ReportFiledMessage
        {
            OwnerId = callerId,
            ListingId = e.Id,
            ListingType = ListingReportTypes.TripEnquiry,
            ReasonName = reason.Name,
            ListingTitle = $"{TripDateText.FirstName(traveller?.Name)} · {e.ListingName}",
            NotifyOwner = false,
        };
        try { await publisher.PublishAsync("report.filed", JsonSerializer.Serialize(message)); }
        catch (Exception) { /* best-effort: the report row is already saved */ }

        return CreatedResponse(new { reportId = report.Id }, $"/api/v1/admin/reports/{report.Id}");
    }
}

// The one-page booking record. Never reads messages, so no UPI id or host phone can leak into it.
public static class TripSummaryRecordBuilder
{
    private const string Both = "both";
    private const string You = "you";

    public static TripSummaryRecordDto Build(
        Trip trip, TripPlan plan, IReadOnlyList<TripEnquiryDto> dtos, IReadOnlyList<TripEnquiry> raw, DateTime nowUtc)
    {
        var start = trip.StartDate ?? plan.Settings?.StartDate;
        var rawById = raw.GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First());
        var record = new TripSummaryRecordDto
        {
            TripId = trip.Id,
            Title = trip.Title,
            City = trip.CityName,
            StartDate = start,
            EndDate = trip.EndDate ?? plan.EndDate,
            Adults = plan.Settings?.Adults,
            Kids = plan.Settings?.Kids,
            BookingState = TripBookingStates.Compute(trip, plan, raw),
            AgreedTotal = TripBookingSummary.Count(plan, raw).AgreedTotal,
            GeneratedAt = nowUtc,
        };

        foreach (var day in plan.Days.OrderBy(d => d.Day))
            record.Days.Add(new TripSummaryDayDto
            {
                Day = day.Day,
                Date = start?.AddDays(day.Day - 1),
                Spots = day.Spots.Select(s => s.Name).ToList(),
                Minutes = day.Spots.Sum(s => s.VisitMinutes),
            });

        var stays = dtos.Where(d => d.Kind == TripEnquiryKinds.Stay && IsListed(d.Status)).ToList();
        var shown = new HashSet<Guid>();
        foreach (var day in plan.Days.Where(d => d.Day >= 1 && d.Day <= plan.Days.Count).OrderBy(d => d.Day))
        {
            var date = start?.AddDays(day.Day - 1);
            var match = Pick(stays.Where(d => Covers(d, day.Day, date)).ToList());
            if (match != null)
            {
                rawById.TryGetValue(match.Id, out var row);
                record.Nights.Add(NightFrom(match, row, day.Day, date, firstNight: shown.Add(match.Id)));
            }
            else if (day.Stay?.IsOwn == true)
            {
                record.Nights.Add(new TripSummaryNightDto
                {
                    Night = day.Day,
                    Date = date,
                    ListingName = day.Stay.Name,
                    IsOwn = true,
                    OwnPhone = day.Stay.OwnPhone,
                    DisplayStatus = TripOwnDisplay,
                });
            }
            else if (day.Stay?.Skipped == true)
            {
                record.Nights.Add(new TripSummaryNightDto
                {
                    Night = day.Day,
                    Date = date,
                    ListingName = NoStayTonight,
                    DisplayStatus = SkippedDisplay,
                });
            }
        }

        var taxiDtos = dtos
            .Where(d => d.Kind == TripEnquiryKinds.Taxi && IsListed(d.Status))
            .GroupBy(d => d.Id).Select(g => g.First());
        foreach (var d in taxiDtos)
        {
            rawById.TryGetValue(d.Id, out var row);
            record.Taxis.Add(TaxiFrom(d, row, plan));
        }
        record.Taxis.AddRange(OwnTaxis(plan));
        return record;
    }

    private const string TripOwnDisplay = "own";
    private const string SkippedDisplay = "skipped";
    private const string NoStayTonight = "No stay tonight";

    // Booked or cancelled rows only; Closed, Expired, Not available and the like stay out of the record.
    private static bool IsListed(string status)
        => TripEnquiryStatuses.IsBooked(status)
            || status is TripEnquiryStatuses.CancelledByTraveller or TripEnquiryStatuses.CancelledByOwner;

    // A booked row beats a cancelled one; among equals the newest (list order) wins.
    private static TripEnquiryDto? Pick(IReadOnlyList<TripEnquiryDto> rows)
        => rows.FirstOrDefault(r => TripEnquiryStatuses.IsBooked(r.Status)) ?? rows.FirstOrDefault();

    private static bool Covers(TripEnquiryDto d, int night, DateOnly? date)
        => date is { } day && d.CheckIn is { } a && d.CheckOut is { } b ? a <= day && day < b : d.Day == night;

    private static TripSummaryNightDto NightFrom(TripEnquiryDto d, TripEnquiry? row, int night, DateOnly? date, bool firstNight)
    {
        var offer = d.AcceptedOffer;
        var lines = offer?.Lines.Select(l => new TripSummaryRoomLineDto { RoomName = l.RoomName, Units = l.Units, Guests = l.Guests }).ToList()
            ?? new List<TripSummaryRoomLineDto>();
        if (lines.Count == 0 && d.PhoneBooking is { } phone && !string.IsNullOrWhiteSpace(phone.RoomName))
            lines.Add(new TripSummaryRoomLineDto
            {
                RoomName = phone.RoomName,
                Units = d.Context.Rooms ?? 1,
                Guests = (d.Context.Adults ?? 0) + (d.Context.Kids ?? 0),
            });

        var (by, at) = ConfirmedOf(row);
        return new TripSummaryNightDto
        {
            Night = night,
            Date = date,
            ListingName = d.ListingName,
            EnquiryId = d.Id,
            DisplayStatus = d.DisplayStatus,
            RoomLines = lines,
            // The total belongs to the whole enquiry: shown once, on its first night.
            AgreedTotal = firstNight ? d.AgreedTotal : null,
            AdvanceStatus = d.Advance.Status,
            AdvanceAmount = d.Advance.Amount,
            CancellationText = offer?.CancellationText,
            GstInvoice = offer?.GstInvoice,
            ConfirmedBy = by,
            ConfirmedAt = at,
            CancelledBy = d.CancelledBy,
        };
    }

    private static TripSummaryTaxiDto TaxiFrom(TripEnquiryDto d, TripEnquiry? row, TripPlan plan)
    {
        var days = d.Days is { Length: > 0 } list ? list.ToList() : d.Day is { } one ? new List<int> { one } : new List<int>();
        var vehicle = plan.Days.Select(x => x.Taxi).FirstOrDefault(t => t != null && t.TaxiId == d.ListingId)?.VehicleTypeName;
        var (by, _) = ConfirmedOf(row);
        return new TripSummaryTaxiDto
        {
            EnquiryId = d.Id,
            ListingName = string.IsNullOrWhiteSpace(d.Owner.Name) ? d.ListingName : d.Owner.Name,
            VehicleTypeName = vehicle,
            Days = days,
            CheckIn = d.CheckIn,
            CheckOut = d.CheckOut,
            DisplayStatus = d.DisplayStatus,
            AgreedFare = d.Taxi?.AgreedFare,
            TollExtra = d.Taxi?.TollExtra ?? false,
            PickupTime = d.Taxi?.PickupTime,
            PickupPlace = d.Taxi?.PickupPlace,
            ConfirmedBy = by,
            CancelledBy = d.CancelledBy,
        };
    }

    // One entry per own taxi (name + phone), listing every plan day it covers.
    private static IEnumerable<TripSummaryTaxiDto> OwnTaxis(TripPlan plan)
        => plan.Days
            .Where(d => d.Taxi?.IsOwn == true)
            .GroupBy(d => (d.Taxi!.Name, d.Taxi.OwnPhone))
            .Select(g => new TripSummaryTaxiDto
            {
                ListingName = g.Key.Name,
                VehicleTypeName = g.First().Taxi!.VehicleTypeName,
                Days = g.Select(d => d.Day).OrderBy(d => d).ToList(),
                IsOwn = true,
                OwnPhone = g.Key.OwnPhone,
                DisplayStatus = TripOwnDisplay,
            });

    private static (string? By, DateTime? At) ConfirmedOf(TripEnquiry? e)
    {
        if (e == null) return (null, null);
        if (e.ConfirmedByTravellerAt is { } t && e.ConfirmedByOwnerAt is { } o) return (Both, t > o ? t : o);
        if (e.ConfirmedByTravellerAt is { } you) return (You, you);
        if (e.BookedByTravellerAt is { } booked) return (You, booked);
        return (null, null);
    }
}
