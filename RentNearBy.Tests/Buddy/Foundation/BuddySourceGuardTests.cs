using System.Text.RegularExpressions;

namespace RentNearBy.Tests.Buddy.Foundation;

// Contract 7.3: source guards that always run (no database) over the feature's service and handler folders.
internal static class BuddySources
{
    public static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RentNearBy.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new Xunit.Sdk.XunitException("RentNearBy.sln not found");
    }

    public static IReadOnlyList<string> Files()
    {
        var root = Root();
        string[] folders =
        [
            Path.Combine(root, "RentNearBy.Infrastructure", "Services", "Buddy"),
            Path.Combine(root, "RentNearBy.Api", "Handlers", "Buddy"),
        ];
        return folders.Where(Directory.Exists)
            .SelectMany(f => Directory.GetFiles(f, "*.cs", SearchOption.AllDirectories))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
    }

    public static string Rel(string path) => Path.GetRelativePath(Root(), path);

    private static readonly Regex LoopStart = new(@"^\s*(for|foreach|while)\s*\(|\bawait\s+foreach\s*\(", RegexOptions.Compiled);

    // Lines with "await" inside the body of for/foreach/while (or any Select(async ...)) not marked "// batch-loop".
    public static IEnumerable<string> AwaitInLoops(string path)
    {
        var lines = File.ReadAllLines(path);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Contains("// batch-loop")) continue;
            if (line.Contains("Select(async"))
            {
                yield return $"{Rel(path)}:{i + 1}: Select(async";
                continue;
            }
            if (!LoopStart.IsMatch(line)) continue;
            if (line.TrimEnd().EndsWith(';')) continue;

            var (start, end) = Body(lines, i);
            for (var j = start; j <= end && j < lines.Length; j++)
                if (Regex.IsMatch(lines[j], @"\bawait\b") && !lines[j].Contains("// batch-loop"))
                    yield return $"{Rel(path)}:{j + 1}: {lines[j].Trim()}";
        }
    }

    private static (int Start, int End) Body(string[] lines, int loopLine)
    {
        var open = lines[loopLine].IndexOf('{') >= 0 ? loopLine : loopLine + 1;
        if (open >= lines.Length || !lines[open].Contains('{'))
            return (loopLine + 1, loopLine + 1);
        var depth = 0;
        for (var j = open; j < lines.Length; j++)
        {
            foreach (var c in lines[j])
            {
                if (c == '{') depth++;
                else if (c == '}') depth--;
            }
            if (depth <= 0) return (open, j);
        }
        return (open, lines.Length - 1);
    }
}

public class NoQueryInLoopGuardTests
{
    [Fact]
    public void Folders_exist() => Assert.NotEmpty(BuddySources.Files());

    [Fact]
    public void No_await_inside_a_loop()
    {
        var hits = BuddySources.Files().SelectMany(BuddySources.AwaitInLoops).ToList();
        Assert.True(hits.Count == 0, "Query or seam call inside a loop (batch the work, or mark a job batch loop with // batch-loop):\n" + string.Join("\n", hits));
    }

    [Fact]
    public void Scanner_finds_an_await_in_a_loop()
    {
        var path = Path.Combine(Path.GetTempPath(), $"buddy_guard_{Guid.NewGuid():N}.cs");
        File.WriteAllLines(path,
        [
            "foreach (var id in ids)",
            "{",
            "    await db.Set<X>().FirstOrDefaultAsync(x => x.Id == id);",
            "}",
            "foreach (var id in ids) // batch-loop",
            "{",
            "    await RunAsync(id);",
            "}",
        ]);
        try
        {
            Assert.Single(BuddySources.AwaitInLoops(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class NoTripsWriteGuardTests
{
    private static readonly Regex[] Forbidden =
    [
        new(@"UPDATE\s+\\?""Trips\\?""", RegexOptions.IgnoreCase),
        new(@"\.Trips\.Update\b"),
        new(@"Trips\.Attach\b"),
        new(@"Set<Trip>\(\)\.(Update|Attach|Remove)\b"),
        new(@"\.Trips\.Remove\b"),
        new(@"\b[tT]rip\.(Title|Status|PlanJson|UpdatedAt|DayCount|SpotCount|StartDate|EndDate|CompletedAt|MutedAt|NameChosen|InviteCode)\s*=[^=]"),
    ];

    [Fact]
    public void Feature_code_never_writes_trips()
    {
        var hits = new List<string>();
        foreach (var file in BuddySources.Files())
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
                if (Forbidden.Any(r => r.IsMatch(lines[i]))) hits.Add($"{BuddySources.Rel(file)}:{i + 1}: {lines[i].Trim()}");
        }
        Assert.True(hits.Count == 0, "This feature never updates Trips:\n" + string.Join("\n", hits));
    }
}

public class NoNotificationTextGuardTests
{
    private static readonly Regex[] Forbidden =
    [
        new(@"new\s+NotificationEvent\b"),
        new(@"NotificationEvents\.(Add|AddRange|Update|Attach)\b"),
        new(@"Set<NotificationEvent>\(\)"),
        new(@"INSERT\s+INTO\s+\\?""NotificationEvents\\?""", RegexOptions.IgnoreCase),
    ];

    [Fact]
    public void Feature_code_never_writes_notification_events()
    {
        var hits = new List<string>();
        foreach (var file in BuddySources.Files())
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
                if (Forbidden.Any(r => r.IsMatch(lines[i]))) hits.Add($"{BuddySources.Rel(file)}:{i + 1}: {lines[i].Trim()}");
        }
        Assert.True(hits.Count == 0, "Stored notifications of this feature are UserNotices rows only:\n" + string.Join("\n", hits));
    }
}
