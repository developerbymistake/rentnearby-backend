using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Core.Interfaces.Social;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services.Social;

// Scoped collector of what goes out after a social transaction commits: chat hub events, DmUnread counts, SocialChanged hints
// and the outbox wake. Services add to it inside the transaction; whoever commits calls FlushAsync after the commit (Clear at
// the start of every attempt, so a retried or rolled-back unit sends nothing).
public sealed class SocialAfterCommit(ApplicationDbContext db, ISocialRealtime realtime, IBuddyLiveHints hints, INoticeWriter notices)
{
    private readonly List<SocialHubEvent> _events = [];
    private readonly List<(string Scope, Guid[] Users)> _hints = [];
    private readonly HashSet<Guid> _unread = [];
    private bool _wake;

    public void Add(SocialHubEvent e) => _events.Add(e);

    public void AddRange(IEnumerable<SocialHubEvent> events) => _events.AddRange(events);

    // Users whose chat unread counts changed: one DmUnread each after commit.
    public void UnreadFor(IEnumerable<Guid> users)
    {
        foreach (var u in users) _unread.Add(u);
    }

    public void Hint(string scope, params Guid[] users) => _hints.Add((scope, users));

    public void Wake() => _wake = true;

    public void Clear()
    {
        _events.Clear();
        _hints.Clear();
        _unread.Clear();
        _wake = false;
    }

    public async Task FlushAsync(CancellationToken ct = default)
    {
        var events = _events.ToList();
        var pending = _hints.ToList();
        var unreadUsers = _unread.ToList();
        var wake = _wake;
        Clear();
        if (wake) notices.Wake();
        if (unreadUsers.Count > 0)
        {
            var counts = await SocialChatService.UnreadManyAsync(db, unreadUsers, ct);
            events.AddRange(unreadUsers.Select(u => new SocialHubEvent(u, SocialHubEventNames.DmUnread, counts.GetValueOrDefault(u) ?? new())));
        }
        if (events.Count > 0) await realtime.SendAsync(events);
        foreach (var group in pending.GroupBy(h => h.Scope)) // batch-loop
            await hints.SocialChangedAsync(group.Key, group.SelectMany(h => h.Users).Distinct().ToList());
    }
}
