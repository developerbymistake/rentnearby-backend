using RentNearBy.Core.DTOs.Buddy;

namespace RentNearBy.Core.Models.Buddy;

public static class MoneyPeople
{
    public const string DeletedName = AccountDeletionRules.DeletedUserName;
    public const string DeletedInitial = "?";

    // Last 4 digits of the username ("Bakhli#48291736" -> "1736"); "" when missing.
    public static string Last4(string? username)
    {
        if (string.IsNullOrEmpty(username)) return string.Empty;
        return username.Length <= 4 ? username : username[^4..];
    }

    public static string Initial(string? name)
    {
        var trimmed = name?.Trim();
        return string.IsNullOrEmpty(trimmed) ? DeletedInitial : char.ToUpperInvariant(trimmed[0]).ToString();
    }

    // Travellers in canonical order, then ids found only in money rows (deleted accounts) by id text.
    public static List<Guid> CanonicalOrder(IReadOnlyList<TripTraveller> travellers, IEnumerable<Guid> moneyIds)
    {
        var order = travellers.Select(t => t.UserId).Distinct().ToList();
        var known = order.ToHashSet();
        order.AddRange(moneyIds.Where(id => !known.Contains(id)).Distinct().OrderBy(id => id, Comparer<Guid>.Create(TripExpenseEngine.CompareIds)));
        return order;
    }

    public static MoneyPersonDto Person(TripTraveller? traveller, Guid userId, Guid callerId)
    {
        if (traveller is null)
            return new MoneyPersonDto
            {
                UserId = userId, Name = DeletedName, Last4 = string.Empty, Initial = DeletedInitial,
                IsMe = userId == callerId, Status = TripAccessStatuses.Deleted,
            };
        return new MoneyPersonDto
        {
            UserId = userId,
            Name = traveller.Name,
            Last4 = Last4(traveller.Username),
            Initial = Initial(traveller.Name),
            PhotoUrl = traveller.PhotoUrl,
            IsMe = userId == callerId,
            Status = traveller.IsOwner ? TripAccessStatuses.Owner : traveller.Status,
        };
    }

    // One MoneyPersonDto per id in canonical order (travellers first, unknown ids last).
    public static List<MoneyPersonDto> Build(IReadOnlyList<TripTraveller> travellers, IEnumerable<Guid> moneyIds, Guid callerId)
    {
        var byId = new Dictionary<Guid, TripTraveller>();
        foreach (var t in travellers) byId.TryAdd(t.UserId, t);
        return CanonicalOrder(travellers, moneyIds)
            .Select(id => Person(byId.GetValueOrDefault(id), id, callerId))
            .ToList();
    }

    // Push text name: plain name, plus the last 4 digits when another traveller of the trip has the same name.
    public static string PushName(TripTraveller person, IReadOnlyList<TripTraveller> travellers)
    {
        var clash = travellers.Any(t => t.UserId != person.UserId && string.Equals(t.Name.Trim(), person.Name.Trim(), StringComparison.OrdinalIgnoreCase));
        var last4 = Last4(person.Username);
        return clash && last4.Length > 0 ? $"{person.Name} {last4}" : person.Name;
    }
}
