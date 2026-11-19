using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Api.Handlers.TaxiRequests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

public static class DeletedAccountLogEvents
{
    public static readonly EventId AdminViewed = new(7201, "deleted_account.admin.viewed");
}

public static class AdminDeletedAccountRules
{
    public const int SearchMax = 100;
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    // A search holding a digit is a phone search (digits only, contains); otherwise a lower-case name search.
    public static (string? PhoneDigits, string? NameLower, bool TooLong) ParseSearch(string? search)
    {
        var s = search?.Trim();
        if (string.IsNullOrEmpty(s)) return (null, null, false);
        if (s.Length > SearchMax) return (null, null, true);
        if (s.Any(char.IsAsciiDigit)) return (new string(s.Where(char.IsAsciiDigit).ToArray()), null, false);
        return (null, s.ToLowerInvariant(), false);
    }

    public static IQueryable<DeletedAccountRecord> Filter(IQueryable<DeletedAccountRecord> query, string? phoneDigits, string? nameLower)
    {
        if (phoneDigits != null) query = query.Where(r => r.PhoneNumber.Contains(phoneDigits));
        if (nameLower != null) query = query.Where(r => r.Name != null && r.Name.ToLower().Contains(nameLower));
        return query;
    }

    public static IQueryable<DeletedAccountRecord> Page(IQueryable<DeletedAccountRecord> query, (DateTime At, Guid Id)? after, int take)
    {
        if (after is var (at, id)) query = query.Where(r => r.DeletedAt < at || (r.DeletedAt == at && r.Id.CompareTo(id) > 0));
        return query.OrderByDescending(r => r.DeletedAt).ThenBy(r => r.Id).Take(take + 1);
    }

    // licence = decrypted plain value (detail only); the list passes null.
    public static DeletedAccountRecordDto ToDto(DeletedAccountRecord r, string? licence) => new()
    {
        Id = r.Id,
        OriginalUserId = r.OriginalUserId,
        PhoneNumber = r.PhoneNumber,
        Name = r.Name,
        Role = r.Role,
        RoleName = UserRoles.NameOf(r.Role),
        AccountCreatedAt = r.AccountCreatedAt,
        DeletedAt = r.DeletedAt,
        HasDrivingLicence = r.DrivingLicenceNumberEnc != null,
        DrivingLicenceNumber = licence == null ? null : TaxiIdentityRules.FormatDl(licence),
        DateOfBirth = r.DateOfBirth is { } dob ? TaxiIdentityRules.FormatDob(dob) : null,
        StayRegistrationTypeName = r.StayRegistrationTypeName,
        StayRegistrationOtherName = r.StayRegistrationOtherName,
        StayRegistrationNumber = r.StayRegistrationNumber,
        StayGstin = r.StayGstin,
    };
}

public static class AdminDeletedAccountHandlers
{
    // Keyset by (DeletedAt desc, Id); an old admin build sending page > 1 without a cursor gets the offset page.
    public static async Task<IResult> GetDeletedAccounts(
        ApplicationDbContext db, CancellationToken ct,
        string? search = null, string? cursor = null, int page = 1, int pageSize = AdminDeletedAccountRules.DefaultPageSize)
    {
        var (phone, name, tooLong) = AdminDeletedAccountRules.ParseSearch(search);
        if (tooLong) return BadRequestResponse("Search is too long");

        (DateTime At, Guid Id)? after = null;
        if (!string.IsNullOrEmpty(cursor))
        {
            if (!AdminTaxiRequestRules.TryDecodeCursor(cursor, out var at, out var id)) return BadRequestResponse("Cursor is not valid");
            after = (at, id);
        }

        var take = Math.Clamp(pageSize, 1, AdminDeletedAccountRules.MaxPageSize);
        var query = AdminDeletedAccountRules.Filter(db.DeletedAccountRecords.AsNoTracking(), phone, name);

        List<DeletedAccountRecord> rows;
        if (after == null && page > 1)
            rows = await query.OrderByDescending(r => r.DeletedAt).ThenBy(r => r.Id)
                .Skip((page - 1) * take).Take(take + 1).ToListAsync(ct);
        else
            rows = await AdminDeletedAccountRules.Page(query, after, take).ToListAsync(ct);

        var hasMore = rows.Count > take;
        rows = rows.Take(take).ToList();
        return OkResponse(new
        {
            items = rows.Select(r => AdminDeletedAccountRules.ToDto(r, null)).ToList(),
            hasMore,
            nextCursor = hasMore && rows.Count > 0 ? AdminTaxiRequestRules.EncodeCursor(rows[^1].DeletedAt, rows[^1].Id) : null,
        });
    }

    public static async Task<IResult> GetDeletedAccount(
        Guid id, ClaimsPrincipal principal, ApplicationDbContext db, IIdentityCipher cipher, ILoggerFactory loggerFactory)
    {
        if (!AdminAuthHandlers.TryGetAdminId(principal, out var adminId)) return UnauthorizedResponse();
        var record = await db.DeletedAccountRecords.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id);
        if (record == null) return NotFoundResponse("Deleted account not found");

        var logger = loggerFactory.CreateLogger(nameof(AdminDeletedAccountHandlers));
        var licence = TaxiHandlers.ReadLicence(cipher, record.DrivingLicenceNumberEnc, record.Id, logger, deletedRecord: true);
        logger.LogInformation(DeletedAccountLogEvents.AdminViewed,
            "Admin {AdminId} viewed deleted account record {RecordId}", adminId, id);
        return OkResponse(AdminDeletedAccountRules.ToDto(record, licence));
    }
}
