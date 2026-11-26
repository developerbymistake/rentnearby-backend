using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.Users;

// In-memory Users table for the username paths: "set if null" is atomic like the SQL UPDATE ... WHERE Username IS NULL.
public class UserRepoStub : DispatchProxy
{
    private readonly object _gate = new();
    public Dictionary<Guid, string?> Stored = [];
    public HashSet<string> Taken = [];
    public List<User> Added = [];
    public User? Row;
    public int SetCalls;
    public Func<Task>? BeforeSet;

    protected override object? Invoke(MethodInfo? m, object?[]? a) => m?.Name switch
    {
        nameof(IUserRepository.SetUsernameIfNullAsync) => SetIfNullAsync((Guid)a![0]!, (string)a[1]!),
        nameof(IUserRepository.GetUsernameAsync) => Task.FromResult(Read((Guid)a![0]!)),
        nameof(IUserRepository.UsernameExistsAsync) => Task.FromResult(IsTaken((string)a![0]!)),
        nameof(IRepository<User>.AddAsync) => AddUser((User)a![0]!),
        nameof(IRepository<User>.GetByIdAsync) => Task.FromResult(Row),
        nameof(IRepository<User>.UpdateAsync) => Task.CompletedTask,
        nameof(IUserRepository.GetByVerifiedPhoneAsync) => Task.FromResult(Row),
        nameof(IUserRepository.IsPhoneVerifiedByAnyUserAsync) => Task.FromResult(false),
        nameof(IUserRepository.GetSocialGenderAsync) => Task.FromResult<string?>(null),
        _ => throw new NotSupportedException(m?.Name),
    };

    private async Task<bool> SetIfNullAsync(Guid id, string name)
    {
        Interlocked.Increment(ref SetCalls);
        if (BeforeSet != null) await BeforeSet();
        lock (_gate)
        {
            if (Taken.Contains(name)) throw UsernameFakes.Clash();
            if (!Stored.ContainsKey(id) || Stored[id] != null) return false;
            Stored[id] = name;
            Taken.Add(name);
            return true;
        }
    }

    private string? Read(Guid id) { lock (_gate) return Stored.GetValueOrDefault(id); }
    private bool IsTaken(string name) { lock (_gate) return Taken.Contains(name); }

    private Task AddUser(User u)
    {
        Added.Add(u);
        return Task.CompletedTask;
    }
}

public class RoleRequestRepoStub : DispatchProxy
{
    protected override object? Invoke(MethodInfo? m, object?[]? a) => m?.Name switch
    {
        nameof(IRoleChangeRequestRepository.GetLatestForUserAsync) => Task.FromResult<RoleChangeRequest?>(null),
        _ => throw new NotSupportedException(m?.Name),
    };
}

public class UowStub : DispatchProxy
{
    public IUserRepository? Users;
    public IRoleChangeRequestRepository? Requests;
    public int Saves;
    // Runs on each SaveChangesAsync; may throw like the database would.
    public Action<UowStub>? OnSave;

    protected override object? Invoke(MethodInfo? m, object?[]? a)
    {
        switch (m?.Name)
        {
            case "get_Users": return Users;
            case "get_RoleChangeRequests": return Requests;
            case "get_Sessions": return Create<ISessionRepository, NoopProxy>();
            case "get_DeviceTokens": return Create<IDeviceTokenRepository, NoopProxy>();
            case nameof(IUnitOfWork.SaveChangesAsync):
                Saves++;
                try
                {
                    OnSave?.Invoke(this);
                }
                catch (Exception ex)
                {
                    return Task.FromException<int>(ex);
                }
                return Task.FromResult(1);
            default:
                throw new NotSupportedException(m?.Name);
        }
    }
}

// Every Task-returning call succeeds and does nothing.
public class NoopProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? m, object?[]? a)
        => m?.ReturnType == typeof(Task) ? Task.CompletedTask : throw new NotSupportedException(m?.Name);
}

public sealed class FakePendingStore : IPendingUsernameStore
{
    public Dictionary<string, (string Name, TimeSpan Ttl)> Rows = [];

    public Task<string?> GetAsync(string phoneNumber)
        => Task.FromResult(Rows.TryGetValue(phoneNumber, out var r) ? r.Name : null);

    public Task SaveAsync(string phoneNumber, string username, TimeSpan ttl)
    {
        Rows[phoneNumber] = (username, ttl);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string phoneNumber)
    {
        Rows.Remove(phoneNumber);
        return Task.CompletedTask;
    }
}

public sealed class UsernameWorld
{
    public IUnitOfWork Uow { get; }
    public UowStub UowState => (UowStub)(object)Uow;
    public UserRepoStub Repo { get; }
    public FakePendingStore Pending { get; } = new();

    public UsernameWorld()
    {
        var users = DispatchProxy.Create<IUserRepository, UserRepoStub>();
        var requests = DispatchProxy.Create<IRoleChangeRequestRepository, RoleRequestRepoStub>();
        Uow = DispatchProxy.Create<IUnitOfWork, UowStub>();
        Repo = (UserRepoStub)(object)users;
        UowState.Users = users;
        UowState.Requests = requests;
    }

    public UsernameService Service(params string[] candidates)
        => new(Uow, Pending) { NextCandidate = candidates.Length == 0 ? Usernames.NewRandom : UsernamesTests.Sequence(candidates) };
}

public static class UsernameFakes
{
    public static PostgresException Clash(string constraint = Usernames.UniqueIndex)
        => new("duplicate key value violates unique constraint", "ERROR", "ERROR",
            PostgresErrorCodes.UniqueViolation, constraintName: constraint);

    public static DbUpdateException SaveClash(string constraint = Usernames.UniqueIndex)
        => new("save failed", Clash(constraint));
}
