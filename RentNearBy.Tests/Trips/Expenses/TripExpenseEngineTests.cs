using System.Runtime.CompilerServices;
using System.Text.Json;

namespace RentNearBy.Tests.Trips.Expenses;

public class TripExpenseEngineTests
{
    private const string VectorFile = "expense_engine_vectors.json";

    private static string VectorPath([CallerFilePath] string thisFile = "") =>
        Path.Combine(Path.GetDirectoryName(thisFile)!, VectorFile);

    private static readonly Lazy<JsonElement[]> Cases = new(() =>
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(VectorPath()));
        Assert.Equal(1, doc.RootElement.GetProperty("version").GetInt32());
        return doc.RootElement.GetProperty("cases").EnumerateArray().Select(c => c.Clone()).ToArray();
    });

    public static TheoryData<int, string> CaseIndex()
    {
        var data = new TheoryData<int, string>();
        for (var i = 0; i < Cases.Value.Length; i++)
            data.Add(i, Cases.Value[i].GetProperty("name").GetString()!);
        return data;
    }

    [Fact]
    public void Vector_file_has_every_kind()
    {
        var kinds = Cases.Value.Select(c => c.GetProperty("kind").GetString()).Distinct().OrderBy(k => k, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "canonicalOrder", "customSplit", "equalSplit", "settle", "shareWith", "sheet" }, kinds);
        Assert.Equal(89, Cases.Value.Length);
    }

    [Theory]
    [MemberData(nameof(CaseIndex))]
    public void Vector(int index, string name)
    {
        var c = Cases.Value[index];
        var names = new Names(c);
        var input = c.GetProperty("in");
        var wantsError = c.TryGetProperty("error", out var err) && err.GetBoolean();

        string Run() => c.GetProperty("kind").GetString() switch
        {
            "equalSplit" => names.Split(TripExpenseEngine.EqualSplit(
                input.GetProperty("amount").GetInt32(),
                names.Ids(input.GetProperty("canonical")),
                names.Ids(input.GetProperty("sharers")),
                names.Id(input.GetProperty("payer").GetString()!))),
            "customSplit" => names.Custom(TripExpenseEngine.CustomSplit(
                input.GetProperty("amount").GetInt32(),
                names.Ids(input.GetProperty("canonical")),
                names.Shares(input.GetProperty("entries")))),
            "shareWith" => names.Split(TripExpenseEngine.ShareWith(
                input.GetProperty("amount").GetInt32(),
                names.Ids(input.GetProperty("canonical")),
                names.Shares(input.GetProperty("stored")),
                names.Id(input.GetProperty("payer").GetString()!),
                names.Id(input.GetProperty("joiner").GetString()!))),
            "canonicalOrder" => JsonSerializer.Serialize(TripExpenseEngine.CanonicalOrder(
                Guid.Parse(input.GetProperty("owner").GetString()!),
                input.GetProperty("members").EnumerateArray().Select(m => (
                    Guid.Parse(m.GetProperty("userId").GetString()!),
                    m.GetProperty("joinedAt").GetDateTime().ToUniversalTime()))).Select(g => g.ToString("D"))),
            "settle" => names.Settle(TripExpenseEngine.Settle(
                input.GetProperty("nets").EnumerateArray()
                    .Select(n => (names.Id(n.GetProperty("userId").GetString()!), n.GetProperty("net").GetInt64())).ToList())),
            "sheet" => names.Sheet(TripExpenseEngine.Sheet(
                names.Ids(input.GetProperty("canonical")),
                input.GetProperty("expenses").EnumerateArray().Select(e => new LedgerExpense(
                    names.Id(e.GetProperty("paidBy").GetString()!),
                    e.GetProperty("amount").GetInt32(),
                    names.Shares(e.GetProperty("shares")))).ToList(),
                input.GetProperty("payments").EnumerateArray().Select(p => new LedgerPayment(
                    names.Id(p.GetProperty("id").GetString()!),
                    names.Id(p.GetProperty("from").GetString()!),
                    names.Id(p.GetProperty("to").GetString()!),
                    p.GetProperty("amount").GetInt32(),
                    Enum.Parse<LedgerPaymentState>(p.GetProperty("state").GetString()!))).ToList())),
            var other => throw new InvalidOperationException($"Unknown kind {other}"),
        };

        Assert.False(string.IsNullOrEmpty(name));
        if (wantsError)
        {
            Assert.Throws<ArgumentException>(() => { Run(); });
            return;
        }
        Assert.Equal(Canonical(c.GetProperty("out")), Run());
        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void Split_18500_by_3_is_6168_6166_6166()
    {
        Guid r = Guid.NewGuid(), a = Guid.NewGuid(), p = Guid.NewGuid();
        var split = TripExpenseEngine.EqualSplit(18_500, [r, a, p], [p, a, r], r);
        Assert.Equal([new SplitShare(r, 6168), new SplitShare(a, 6166), new SplitShare(p, 6166)], split.Shares);
        Assert.Equal(2, split.Leftover);
        Assert.Equal(r, split.LeftoverUserId);
    }

    [Fact]
    public void Settle_random_inputs_clear_every_balance_and_stay_stable()
    {
        var rnd = new Random(20261120);
        for (var round = 0; round < 300; round++)
        {
            var n = rnd.Next(2, 21);
            var ids = Enumerable.Range(0, n).Select(_ => Guid.NewGuid()).ToList();
            var nets = new long[n];
            for (var i = 0; i < n - 1; i++)
            {
                nets[i] = rnd.Next(-9, 10) * (long)rnd.Next(1, 4000);
                nets[n - 1] -= nets[i];
            }
            var input = ids.Select((id, i) => (id, nets[i])).ToList();
            var first = TripExpenseEngine.Settle(input);
            var second = TripExpenseEngine.Settle(input);
            Assert.Equal(first.Payments, second.Payments);

            var people = nets.Count(v => v != 0);
            Assert.Equal(people <= TripExpenseEngine.ExactSettleMaxPeople, first.Exact);
            Assert.True(first.Payments.Count <= Math.Max(0, people - 1));

            var left = ids.Zip(nets).ToDictionary(x => x.First, x => x.Second);
            foreach (var pay in first.Payments)
            {
                Assert.True(pay.Amount > 0);
                Assert.True(left[pay.FromUserId] < 0 && left[pay.ToUserId] > 0);
                left[pay.FromUserId] += pay.Amount;
                left[pay.ToUserId] -= pay.Amount;
            }
            Assert.All(left.Values, v => Assert.Equal(0, v));

            var order = first.Payments.Select(x => (ids.IndexOf(x.FromUserId), ids.IndexOf(x.ToUserId))).ToList();
            Assert.Equal(order.OrderBy(x => x.Item1).ThenBy(x => x.Item2), order);
        }
    }

    [Fact]
    public void Settle_worst_case_of_14_people_is_fast()
    {
        var input = Enumerable.Range(0, 14).Select(i => (Guid.NewGuid(), (long)(i % 2 == 0 ? 1 : -1))).ToList();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = TripExpenseEngine.Settle(input);
        watch.Stop();
        Assert.Equal(7, result.Payments.Count);
        Assert.True(watch.ElapsedMilliseconds < 2000, $"{watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Open_amount_reads_the_pair()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        var open = new[] { new OpenPayment(a, b, 500) };
        Assert.Equal(500, TripExpenseEngine.OpenAmount(open, a, b));
        Assert.Equal(0, TripExpenseEngine.OpenAmount(open, b, a));
    }

    private static string Canonical(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => JsonSerializer.Serialize(p.Name) + ":" + Canonical(p.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", e.EnumerateArray().Select(Canonical)) + "]",
        _ => e.GetRawText(),
    };

    private sealed class Names
    {
        private static readonly HashSet<string> IdKeys = new(StringComparer.Ordinal)
        {
            "userId", "payer", "joiner", "paidBy", "from", "to", "id", "canonical", "sharers",
        };

        private readonly Dictionary<string, Guid> _ids = new(StringComparer.Ordinal);
        private readonly Dictionary<Guid, string> _names = new();

        public Names(JsonElement testCase)
        {
            var found = new SortedSet<string>(StringComparer.Ordinal);
            Collect(testCase.GetProperty("in"), null, found);
            var rank = 1;
            foreach (var name in found)
            {
                var id = new Guid(rank++, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1);
                _ids[name] = id;
                _names[id] = name;
            }
        }

        private static void Collect(JsonElement e, string? key, SortedSet<string> found)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var p in e.EnumerateObject()) Collect(p.Value, p.Name, found);
                    break;
                case JsonValueKind.Array:
                    foreach (var item in e.EnumerateArray()) Collect(item, key, found);
                    break;
                case JsonValueKind.String when key != null && IdKeys.Contains(key):
                    found.Add(e.GetString()!);
                    break;
            }
        }

        public Guid Id(string name) => _ids[name];

        public List<Guid> Ids(JsonElement array) => array.EnumerateArray().Select(x => Id(x.GetString()!)).ToList();

        public List<SplitShare> Shares(JsonElement array) => array.EnumerateArray()
            .Select(x => new SplitShare(Id(x.GetProperty("userId").GetString()!), x.GetProperty("share").GetInt32())).ToList();

        private static SortedDictionary<string, object?> Map() => new(StringComparer.Ordinal);

        private object Share(SplitShare s)
        {
            var m = Map();
            m["share"] = s.Share;
            m["userId"] = _names[s.UserId];
            return m;
        }

        private object Pay(OpenPayment p)
        {
            var m = Map();
            m["amount"] = p.Amount;
            m["from"] = _names[p.FromUserId];
            m["to"] = _names[p.ToUserId];
            return m;
        }

        public string Split(ExpenseSplit s)
        {
            var m = Map();
            m["each"] = s.Each;
            m["leftover"] = s.Leftover;
            m["leftoverUserId"] = s.LeftoverUserId is { } id ? _names[id] : null;
            m["shares"] = s.Shares.Select(Share).ToList();
            return JsonSerializer.Serialize(m);
        }

        public string Custom(CustomSplitCheck s)
        {
            var m = Map();
            m["assigned"] = s.Assigned;
            m["difference"] = s.Difference;
            m["isValid"] = s.IsValid;
            m["shares"] = s.Shares.Select(Share).ToList();
            return JsonSerializer.Serialize(m);
        }

        public string Settle(SettleResult s)
        {
            var m = Map();
            m["exact"] = s.Exact;
            m["payments"] = s.Payments.Select(Pay).ToList();
            return JsonSerializer.Serialize(m);
        }

        public string Sheet(BalanceSheet s)
        {
            var who = Map();
            who["done"] = s.WhoPaysWhom.Done;
            who["rows"] = s.WhoPaysWhom.Rows.Select(r =>
            {
                var row = Map();
                row["amount"] = r.Amount;
                row["from"] = _names[r.FromUserId];
                row["paymentId"] = r.PaymentId is { } id ? _names[id] : null;
                row["state"] = r.State.ToString();
                row["to"] = _names[r.ToUserId];
                return row;
            }).ToList();
            who["total"] = s.WhoPaysWhom.Total;

            var m = Map();
            m["allSettled"] = s.AllSettled;
            m["exact"] = s.Exact;
            m["open"] = s.Open.Select(Pay).ToList();
            m["people"] = s.People.Select(p =>
            {
                var row = Map();
                row["expenseNet"] = p.ExpenseNet;
                row["got"] = p.Got;
                row["paid"] = p.Paid;
                row["sent"] = p.Sent;
                row["settleNet"] = p.SettleNet;
                row["share"] = p.Share;
                row["userId"] = _names[p.UserId];
                return row;
            }).ToList();
            m["whoPaysWhom"] = who;
            return JsonSerializer.Serialize(m);
        }
    }
}
