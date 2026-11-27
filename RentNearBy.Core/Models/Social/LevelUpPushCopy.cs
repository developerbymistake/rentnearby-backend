namespace RentNearBy.Core.Models.Social;

public sealed record LevelUpCopy(string Key, string Title, string Body);

// Spec C: the congratulation sent once per reached level (push + Notifications inbox row). Tap opens the Travel levels page.
public static class LevelUpPushCopy
{
    public const string Route = UserNotificationRoutes.TravelLevels;
    public const string ArgsJson = "{}";

    public static string Key(Guid userId, int level) => $"levelup:{userId:N}:{level}";

    public static string Tag(Guid userId) => $"levelup:{userId:N}";

    public static LevelUpCopy For(Guid userId, int level, int completedTrips, string? name)
    {
        var hello = string.IsNullOrWhiteSpace(name) ? "Congratulations!" : $"Congratulations, {SocialRules.FirstName(name)}!";
        var (title, body) = level switch
        {
            1 => ("Your first trip is done",
                $"{hello} You completed your first trip and reached {TravelLevels.Name(1)}. Keep exploring."),
            TravelLevels.Highest => ("You are the Travel King",
                $"{hello} {completedTrips} trips completed. You have reached the highest level."),
            _ => ("New level reached",
                $"{hello} You are now a {TravelLevels.Name(level)} with {completedTrips} trips completed. Keep exploring."),
        };
        return new LevelUpCopy(Key(userId, level), title, body);
    }
}
