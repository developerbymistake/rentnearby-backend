using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services.Owner;

// Lane BE1 (7.4, 8.1). Registered as a SINGLETON and stateless: every call works on the context it is given, inside that
// context's transaction, so the singleton OwnerCalendarSyncInterceptor can call it on any pooled context.
public interface IOwnerCalendarSync
{
    // Upsert or cancel the Auto entry of one Stay enquiry or confirmed taxi lead. Idempotent, order-free, never touches money columns.
    Task SyncEnquiryAsync(ApplicationDbContext db, Guid enquiryId, CancellationToken ct = default);

    // Upsert or cancel the Auto entry of one taxi booking (called after each of the three TaxiBookings writers).
    Task SyncTaxiBookingAsync(ApplicationDbContext db, Guid bookingId, CancellationToken ct = default);

    // Dates of the driver's manual entries inside [from, to] (amber line of UC-T-38); one indexed read.
    Task<IReadOnlyList<DateOnly>> ManualDatesAsync(ApplicationDbContext db, Guid taxiListingId, DateOnly from, DateOnly to, CancellationToken ct = default);
}
