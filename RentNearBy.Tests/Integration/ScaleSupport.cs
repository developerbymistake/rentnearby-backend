using System.Data.Common;
using System.Diagnostics;
using System.Text;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Tests.Integration;

// Shared pieces of the scale harness: sizes from the environment, a bounded driver, percentiles, RSS sampling, EXPLAIN ANALYZE.
public static class ScaleSupport
{
    public const string UsersVar = "RNB_SCALE_USERS";
    public const string HostsVar = "RNB_SCALE_HOSTS";
    public const string RowsVar = "RNB_SCALE_REMINDER_ROWS";
    public const string P95BudgetMsVar = "RNB_SCALE_P95_MS";

    public static int Env(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : fallback;

    public sealed record Result(int Total, int Failed, double[] SortedMs, TimeSpan Wall)
    {
        public double P50 => Percentile(SortedMs, 0.50);
        public double P95 => Percentile(SortedMs, 0.95);
        public double P99 => Percentile(SortedMs, 0.99);
        public double Max => SortedMs.Length == 0 ? 0 : SortedMs[^1];
        public double PerSecond => Wall.TotalSeconds <= 0 ? 0 : Total / Wall.TotalSeconds;
    }

    public static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        var rank = (int)Math.Ceiling(p * sorted.Length) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }

    // Up to `concurrency` items in flight (concurrency = items means every virtual user at the same moment); latency is per item.
    public static async Task<Result> DriveAsync(int items, int concurrency, Func<int, Task<bool>> work)
    {
        var latencies = new double[items];
        var failed = 0;
        using var gate = new SemaphoreSlim(Math.Min(concurrency, items));
        var wall = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, items).Select(async i =>
        {
            await gate.WaitAsync();
            var sw = Stopwatch.StartNew();
            try
            {
                if (!await work(i)) Interlocked.Increment(ref failed);
            }
            catch
            {
                Interlocked.Increment(ref failed);
            }
            finally
            {
                latencies[i] = sw.Elapsed.TotalMilliseconds;
                gate.Release();
            }
        }).ToArray();
        await Task.WhenAll(tasks);
        wall.Stop();
        Array.Sort(latencies);
        return new Result(items, failed, latencies, wall.Elapsed);
    }

    // Runs body with 1 s working-set samples; returns the peak and the growth over the start, in MB.
    public static async Task<(long PeakMb, long GrowthMb)> WithRssAsync(Func<Task> body)
    {
        var proc = Process.GetCurrentProcess();
        proc.Refresh();
        var first = proc.WorkingSet64;
        var peak = first;
        using var stop = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try { await Task.Delay(1000, stop.Token); } catch (OperationCanceledException) { break; }
                proc.Refresh();
                peak = Math.Max(peak, proc.WorkingSet64);
            }
        });
        await body();
        stop.Cancel();
        await sampler;
        GC.Collect();
        proc.Refresh();
        peak = Math.Max(peak, proc.WorkingSet64);
        return (peak / (1024 * 1024), (proc.WorkingSet64 - first) / (1024 * 1024));
    }

    public static async Task<string> ExplainAnalyzeAsync(ApplicationDbContext db, string sql)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        await using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = "EXPLAIN (ANALYZE, BUFFERS, FORMAT TEXT) " + sql;
        var sb = new StringBuilder();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) sb.AppendLine(r.GetString(0));
        return sb.ToString();
    }

    public static void Report(ITestOutputHelper output, string title, Result r)
        => output.WriteLine(
            $"{title}: n={r.Total} failed={r.Failed} wall={r.Wall.TotalSeconds:F1}s rate={r.PerSecond:F0}/s " +
            $"p50={r.P50:F0}ms p95={r.P95:F0}ms p99={r.P99:F0}ms max={r.Max:F0}ms");
}
