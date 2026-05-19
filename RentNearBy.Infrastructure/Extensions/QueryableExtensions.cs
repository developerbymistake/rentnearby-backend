using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Responses;

namespace RentNearBy.Infrastructure.Extensions;

public static class QueryableExtensions
{
    public static async Task<PagedResult<T>> ToPagedResultAsync<T>(
        this IQueryable<T> query, int page, int pageSize)
    {
        var total = await query.CountAsync();
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
        return new PagedResult<T>
        {
            Items = items,
            TotalCount = total,
            Page = page,
            PageSize = pageSize,
            HasMore = (page * pageSize) < total,
        };
    }

    public static async Task<PagedResult<TResult>> ToPagedResultAsync<TSource, TResult>(
        this IQueryable<TSource> query, int page, int pageSize, Func<TSource, TResult> selector)
    {
        var total = await query.CountAsync();
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
        return new PagedResult<TResult>
        {
            Items = items.Select(selector).ToList(),
            TotalCount = total,
            Page = page,
            PageSize = pageSize,
            HasMore = (page * pageSize) < total,
        };
    }
}
