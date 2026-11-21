using System.Text.RegularExpressions;
using RentNearBy.Tests.Buddy.Foundation;

namespace RentNearBy.Tests.Owner.Foundation;

// Contract 0.3 and 11.5 source guards over the owner folders (always run, no database). Lanes add files; the guards keep them honest.
internal static class OwnerSources
{
    public static IReadOnlyList<string> Files(params string[][] folders)
    {
        var root = BuddySources.Root();
        return folders.Select(parts => Path.Combine([root, .. parts])).Where(Directory.Exists)
            .SelectMany(f => Directory.GetFiles(f, "*.cs", SearchOption.AllDirectories))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
    }

    // Services and handlers of this work (the pushes, loops and module rules apply here).
    public static IReadOnlyList<string> Code() => Files(
        ["RentNearBy.Infrastructure", "Services", "Owner"],
        ["RentNearBy.Infrastructure", "Services", "Retention", "Owner"],
        ["RentNearBy.Api", "Handlers", "Owner"],
        ["RentNearBy.Api", "Endpoints", "Owner"]);

    // Every file of this work (module split).
    public static IReadOnlyList<string> All() => Files(
        ["RentNearBy.Core", "Entities", "Owner"],
        ["RentNearBy.Core", "Models", "Owner"],
        ["RentNearBy.Core", "DTOs", "Owner"],
        ["RentNearBy.Core", "Interfaces", "Owner"],
        ["RentNearBy.Infrastructure", "Services", "Owner"],
        ["RentNearBy.Api", "Handlers", "Owner"],
        ["RentNearBy.Api", "Endpoints", "Owner"],
        ["RentNearBy.Api", "Validators", "Owner"]);
}

public class OwnerNoQueryInLoopGuardTests
{
    [Fact]
    public void Folders_exist() => Assert.NotEmpty(OwnerSources.Code());

    [Fact]
    public void No_await_inside_a_loop()
    {
        var hits = OwnerSources.Code().SelectMany(BuddySources.AwaitInLoops).ToList();
        Assert.True(hits.Count == 0, "Query or seam call inside a loop (batch the work, or mark a job batch loop with // batch-loop on the loop line):\n" + string.Join("\n", hits));
    }
}

public class NoDirectPushGuardTests
{
    private static readonly Regex Forbidden = new(
        @"\b(IFcmService|FcmService|IPushSender|IRabbitMqPublisher|ConfirmedRabbitPublisher|NotificationEvents?)\b", RegexOptions.Compiled);

    [Fact]
    public void Owner_code_pushes_only_through_the_outbox()
    {
        var hits = new List<string>();
        foreach (var file in OwnerSources.Code())
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
                if (Forbidden.IsMatch(lines[i]) && !lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal))
                    hits.Add($"{BuddySources.Rel(file)}:{i + 1}: {lines[i].Trim()}");
        }
        Assert.True(hits.Count == 0, "Owner code never calls FCM, RabbitMQ or NotificationEvents directly (IOwnerNotifier only):\n" + string.Join("\n", hits));
    }
}

// Contract 0.3.9: Stay and Taxi are separate surfaces. A Stay* file of this work never names a type declared in a Taxi* file of this
// work, and the reverse; shared code lives in kind-agnostic files.
public class ModuleSplitGuardTests
{
    private static readonly Regex Declared = new(@"\b(?:class|record|interface|struct|enum)\s+(\w+)", RegexOptions.Compiled);

    private static HashSet<string> TypesOf(IEnumerable<string> files)
        => files.SelectMany(f => Declared.Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value)).ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void Stay_files_never_name_taxi_types_and_taxi_files_never_name_stay_types()
    {
        var files = OwnerSources.All();
        var stay = files.Where(f => Path.GetFileName(f).StartsWith("Stay", StringComparison.Ordinal)).ToList();
        var taxi = files.Where(f => Path.GetFileName(f).StartsWith("Taxi", StringComparison.Ordinal)).ToList();
        var stayTypes = TypesOf(stay);
        var taxiTypes = TypesOf(taxi);
        var hits = new List<string>();
        foreach (var (group, foreign) in new[] { (stay, taxiTypes), (taxi, stayTypes) })
            foreach (var file in group)
            {
                var text = File.ReadAllText(file);
                hits.AddRange(foreign.Where(t => Regex.IsMatch(text, $@"\b{Regex.Escape(t)}\b")).Select(t => $"{BuddySources.Rel(file)} names {t}"));
            }
        Assert.True(hits.Count == 0, "Stay and Taxi modules must not reference each other:\n" + string.Join("\n", hits));
    }
}

// Contract 0.3.5 and 11.5: money notes are owner-only. No DTO outside the owner calendar DTO files, and no admin handler, carries them.
// Lane BE2 adds the traveller-DTO scan of the listed breakdown (AU-14a).
public class MoneyNotePrivacyGuardTests
{
    private static readonly Regex MoneyMember = new(
        @"\b(FinalAmount|AdvanceReceived|PaidInFull|CalendarEntry)\b", RegexOptions.Compiled);

    [Fact]
    public void Money_note_members_live_only_in_owner_calendar_dtos()
    {
        var root = BuddySources.Root();
        var dtoFiles = Directory.GetFiles(Path.Combine(root, "RentNearBy.Core", "DTOs"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !Regex.IsMatch(Path.GetFileName(f), @"^(Stay|Taxi)CalendarDtos\.cs$"));
        var adminHandlers = Directory.GetFiles(Path.Combine(root, "RentNearBy.Api", "Handlers"), "Admin*.cs", SearchOption.AllDirectories);
        var hits = new List<string>();
        foreach (var file in dtoFiles.Concat(adminHandlers))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!MoneyMember.IsMatch(lines[i])) continue;
                hits.Add($"{BuddySources.Rel(file)}:{i + 1}: {lines[i].Trim()}");
            }
        }
        Assert.True(hits.Count == 0, "Money notes are owner-only (Core/DTOs/Owner/*CalendarDtos.cs):\n" + string.Join("\n", hits));
    }

    [Fact]
    public void Owner_push_carries_no_money()
    {
        var props = typeof(RentNearBy.Core.Interfaces.Owner.OwnerPush).GetConstructors().Single().GetParameters()
            .Where(p => p.ParameterType == typeof(int) || p.ParameterType == typeof(int?) || p.ParameterType == typeof(decimal))
            .Select(p => p.Name);
        Assert.Empty(props);
    }
}
