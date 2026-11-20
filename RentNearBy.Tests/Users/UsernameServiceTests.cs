using Microsoft.EntityFrameworkCore;
using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.Users;

public class UsernameServiceTests
{
    private const string Phone = "9876543210";

    private static User NewUser(string? username = null) => new()
    {
        Id = Guid.NewGuid(), PhoneNumber = Phone, Name = "Asha", Username = username, IsPhoneVerified = true,
    };

    // ---- lazy backfill (existing users) ----

    [Fact]
    public async Task Ensure_keeps_an_existing_username_without_touching_the_database()
    {
        var w = new UsernameWorld();
        var user = NewUser("Bakhli#48291736");

        Assert.Equal("Bakhli#48291736", await w.Service("Bakhli#11111111").EnsureAsync(user));
        Assert.Equal(0, w.Repo.SetCalls);
    }

    [Fact]
    public async Task Ensure_backfills_a_user_without_one()
    {
        var w = new UsernameWorld();
        var user = NewUser();
        w.Repo.Stored[user.Id] = null;

        var name = await w.Service("Bakhli#48291736").EnsureAsync(user);

        Assert.Equal("Bakhli#48291736", name);
        Assert.Equal("Bakhli#48291736", user.Username);
        Assert.Equal("Bakhli#48291736", w.Repo.Stored[user.Id]);
    }

    [Fact]
    public async Task Ensure_retries_when_the_candidate_belongs_to_someone_else()
    {
        var w = new UsernameWorld();
        var user = NewUser();
        w.Repo.Stored[user.Id] = null;
        w.Repo.Taken.Add("Bakhli#11111111");

        var name = await w.Service("Bakhli#11111111", "Bakhli#22222222").EnsureAsync(user);

        Assert.Equal("Bakhli#22222222", name);
        Assert.Equal(2, w.Repo.SetCalls);
    }

    [Fact]
    public async Task Ensure_returns_the_winner_when_another_request_set_it_first()
    {
        var w = new UsernameWorld();
        var user = NewUser();
        w.Repo.Stored[user.Id] = "Bakhli#99999999";

        Assert.Equal("Bakhli#99999999", await w.Service("Bakhli#11111111").EnsureAsync(user));
        Assert.Equal("Bakhli#99999999", user.Username);
    }

    [Fact]
    public async Task Two_parallel_reads_end_with_one_username()
    {
        var w = new UsernameWorld();
        var id = Guid.NewGuid();
        w.Repo.Stored[id] = null;
        var both = new TaskCompletionSource();
        var arrived = 0;
        w.Repo.BeforeSet = async () =>
        {
            if (Interlocked.Increment(ref arrived) == 2) both.SetResult();
            await both.Task;
        };
        var a = new User { Id = id, PhoneNumber = Phone };
        var b = new User { Id = id, PhoneNumber = Phone };

        var results = await Task.WhenAll(
            Task.Run(() => w.Service("Bakhli#11111111").EnsureAsync(a)),
            Task.Run(() => w.Service("Bakhli#22222222").EnsureAsync(b)));

        Assert.Equal(results[0], results[1]);
        Assert.Equal(w.Repo.Stored[id], results[0]);
        Assert.Equal(a.Username, b.Username);
    }

    [Fact]
    public async Task Ensure_for_a_deleted_user_is_not_found()
    {
        var w = new UsernameWorld();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => w.Service("Bakhli#11111111").EnsureAsync(NewUser()));
    }

    // ---- reservation at OTP verify (new users) ----

    [Fact]
    public async Task Reserve_holds_a_fresh_username_for_the_phone()
    {
        var w = new UsernameWorld();

        var name = await w.Service("Bakhli#48291736").ReserveForSignupAsync(Phone);

        Assert.Equal("Bakhli#48291736", name);
        Assert.Equal(("Bakhli#48291736", Usernames.ReservationTtl), w.Pending.Rows[Phone]);
    }

    [Fact]
    public async Task Reserve_again_keeps_the_same_username()
    {
        var w = new UsernameWorld();
        await w.Pending.SaveAsync(Phone, "Bakhli#48291736", TimeSpan.FromMinutes(1));

        Assert.Equal("Bakhli#48291736", await w.Service("Bakhli#11111111").ReserveForSignupAsync(Phone));
        Assert.Equal(Usernames.ReservationTtl, w.Pending.Rows[Phone].Ttl);
    }

    [Fact]
    public async Task Reserve_skips_names_already_used_by_a_saved_user()
    {
        var w = new UsernameWorld();
        await w.Pending.SaveAsync(Phone, "Bakhli#48291736", TimeSpan.FromMinutes(1));
        w.Repo.Taken.UnionWith(["Bakhli#48291736", "Bakhli#11111111"]);

        Assert.Equal("Bakhli#22222222",
            await w.Service("Bakhli#11111111", "Bakhli#22222222").ReserveForSignupAsync(Phone));
        Assert.Equal("Bakhli#22222222", w.Pending.Rows[Phone].Name);
    }

    // ---- new user creation (complete onboarding) ----

    [Fact]
    public async Task New_user_is_saved_with_the_reserved_username_in_the_same_save()
    {
        var w = new UsernameWorld();
        await w.Pending.SaveAsync(Phone, "Bakhli#48291736", Usernames.ReservationTtl);
        var user = NewUser();
        string? usernameAtSave = null;
        w.UowState.OnSave = _ => usernameAtSave = user.Username;

        await w.Service("Bakhli#11111111").SaveNewUserAsync(user);

        Assert.Equal([user], w.Repo.Added);
        Assert.Equal(1, w.UowState.Saves);
        Assert.Equal("Bakhli#48291736", usernameAtSave);
        Assert.Equal("Bakhli#48291736", user.Username);
        Assert.False(w.Pending.Rows.ContainsKey(Phone));
    }

    [Fact]
    public async Task New_user_from_an_old_app_without_a_reservation_still_gets_one()
    {
        var w = new UsernameWorld();
        var user = NewUser();

        await w.Service("Bakhli#48291736").SaveNewUserAsync(user);

        Assert.Equal("Bakhli#48291736", user.Username);
    }

    [Fact]
    public async Task New_user_retries_with_a_new_name_on_a_username_clash()
    {
        var w = new UsernameWorld();
        await w.Pending.SaveAsync(Phone, "Bakhli#48291736", Usernames.ReservationTtl);
        var user = NewUser();
        w.UowState.OnSave = s => { if (s.Saves == 1) throw UsernameFakes.SaveClash(); };

        await w.Service("Bakhli#22222222").SaveNewUserAsync(user);

        Assert.Equal(2, w.UowState.Saves);
        Assert.Single(w.Repo.Added);
        Assert.Equal("Bakhli#22222222", user.Username);
    }

    [Fact]
    public async Task Phone_clash_is_not_retried_and_keeps_the_reservation()
    {
        var w = new UsernameWorld();
        await w.Pending.SaveAsync(Phone, "Bakhli#48291736", Usernames.ReservationTtl);
        w.UowState.OnSave = _ => throw UsernameFakes.SaveClash("IX_Users_PhoneNumber");

        await Assert.ThrowsAsync<DbUpdateException>(() => w.Service("Bakhli#22222222").SaveNewUserAsync(NewUser()));

        Assert.Equal(1, w.UowState.Saves);
        Assert.True(w.Pending.Rows.ContainsKey(Phone));
    }

    [Fact]
    public async Task Six_username_clashes_end_with_a_clear_error()
    {
        var w = new UsernameWorld();
        w.UowState.OnSave = _ => throw UsernameFakes.SaveClash();

        await Assert.ThrowsAsync<UsernameUnavailableException>(() => w.Service().SaveNewUserAsync(NewUser()));
        Assert.Equal(Usernames.MaxAttempts, w.UowState.Saves);
    }

    [Fact]
    public void Clash_detection_needs_the_username_index()
    {
        Assert.True(UsernameService.IsUsernameClash(UsernameFakes.Clash()));
        Assert.True(UsernameService.IsUsernameClash(UsernameFakes.SaveClash()));
        Assert.False(UsernameService.IsUsernameClash(UsernameFakes.SaveClash("IX_Users_PhoneNumber")));
        Assert.False(UsernameService.IsUsernameClash(new InvalidOperationException("x")));
    }
}
