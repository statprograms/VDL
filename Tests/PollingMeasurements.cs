using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Common.Utils.VDL.OS;
using VDL;
using VDL.Cli;

internal static class PollingMeasurements
{
    private sealed record Timing(string Name, int Samples, double MedianMs, double P95Ms,
        double MeanMs, double AllocatedBytesPerCall);
    private sealed record SearchRun(string Kind, int Readers, int InjectedDelayMs, int FreshnessMs,
        int Collections, int Requests, double ElapsedMs, double MedianRequestMs, double P95RequestMs,
        double CollectorWallMs, double FinalFreshnessMs);

    public static async Task RunAsync(string outputPath)
    {
        // Benchmark-only access to the actual uncached collector; no native windows are modified.
        var collect = typeof(SystemWindows).GetMethod("FetchWindowsDataProc", BindingFlags.NonPublic | BindingFlags.Static)!
            .CreateDelegate<Func<IEnumerable<WData>>>();
        var windows = collect().ToArray();
        var stableWindow = new WData { PID = uint.MaxValue, Handle = new IntPtr(long.MaxValue), IsMain = true, IsVisible = true };
        var synthetic = windows.Append(stableWindow).ToArray();
        var timings = new List<Timing>
        {
            Measure("Native window collection", 150, () => collect().Count()),
            Measure("Collection plus snapshot indexing", 150, () => new WindowSnapshot(1, Stopwatch.GetTimestamp(), collect()).Windows.Count),
            Measure("Process discovery without module paths", 20, () => SystemProcesses.GetProcesses(false).Count()),
            Measure("Process discovery with module paths", 20, () => SystemProcesses.GetProcesses(true).Count())
        };
        var hot = new WindowSnapshotCache(collect, TimeSpan.FromMinutes(1));
        hot.GetSnapshot();
        timings.Add(Measure("Fresh cache read", 20000, () => hot.GetSnapshot().Windows.Count));
        foreach (var timing in timings)
            Console.WriteLine($"{timing.Name}: median={timing.MedianMs:F4}ms p95={timing.P95Ms:F4}ms allocation/call={timing.AllocatedBytesPerCall:F0}B");

        var runs = new List<SearchRun>();
        foreach (int readers in new[] { 1, 8, 16 })
        {
            foreach (bool share in new[] { false, true })
            {
                var result = await ObserveAsync(share ? "Native shared" : "Native independent", readers, 0, 25,
                    collect, false);
                runs.Add(result); Print(result);
            }
        }
        foreach (int delay in new[] { 5, 20, 50 })
        {
            foreach (int freshness in new[] { 25, 50, 100 })
            {
                for (int repeat = 0; repeat < 2; repeat++)
                {
                    var result = await ObserveAsync("Synthetic stable search", 8, delay, freshness,
                        () => synthetic, true);
                    runs.Add(result); Print(result);
                }
            }
        }

        foreach (int delay in new[] { 5, 20, 50 })
        {
            for (int repeat = 0; repeat < 2; repeat++)
            {
                var result = await ObserveAsync("Synthetic adaptive stable search", 8, delay, 25,
                    () => synthetic, true, adaptive: true);
                runs.Add(result); Print(result);
            }
        }

        var cli = new CommandLine();
        var options = cli.GetOptions(CliCommands.RootCommand.Parse(["move-left", "--title-start", "Total Commander"]));
        DesktopCommandReceiver.CreateFilters(options, out var processFilters, out var windowFilters);
        var liveSearches = new List<double>();
        for (int i = 0; i < 3; i++)
        {
            SystemWindows.InvalidateSnapshot();
            var sw = Stopwatch.StartNew();
            var matches = await DesktopCommandReceiver.FindStableWindowsProcAsync(options, processFilters, windowFilters, CancellationToken.None);
            if (matches.Any()) liveSearches.Add(sw.Elapsed.TotalMilliseconds);
        }
        var report = new
        {
            TimestampUtc = DateTimeOffset.UtcNow, OS = RuntimeInformation.OSDescription,
            Runtime = RuntimeInformation.FrameworkDescription, LogicalProcessors = Environment.ProcessorCount,
            WindowCount = windows.Length, ProcessCount = SystemProcesses.GetProcesses(false).Count(),
            Notes = "Read-only Release-mode measurements. Native independent/shared runs poll for 800ms. " +
                "Synthetic runs inject blocking collector delay and wait for 500ms main-window stability; " +
                "they model elapsed collection cost, not a full slower PC or CPU consumption. " +
                "Hot-read microbenchmark holds its snapshot fresh for one minute to isolate cache-hit overhead.",
            Timings = timings, Runs = runs, LiveTitleSearchMs = liveSearches
        };
        var destination = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllTextAsync(destination, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Saved measurements to {destination}");
    }

    private static Timing Measure(string name, int count, Func<int> operation)
    {
        for (int i = 0; i < 5; i++) operation();
        var samples = new double[count];
        long allocated = GC.GetTotalAllocatedBytes(true);
        for (int i = 0; i < count; i++)
        {
            long start = Stopwatch.GetTimestamp();
            operation();
            samples[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        allocated = GC.GetTotalAllocatedBytes(true) - allocated;
        Array.Sort(samples);
        return new(name, count, samples[count / 2], samples[(int)Math.Ceiling(count * .95) - 1], samples.Average(), (double)allocated / count);
    }

    private static async Task<SearchRun> ObserveAsync(string kind, int readers, int injectedDelay,
        int freshness, Func<IEnumerable<WData>> source, bool waitForStability, bool adaptive = false)
    {
        int collections = 0;
        long collectorTicks = 0;
        IEnumerable<WData> Collect()
        {
            long start = Stopwatch.GetTimestamp();
            if (injectedDelay > 0) Thread.Sleep(injectedDelay);
            var result = source().ToArray();
            Interlocked.Increment(ref collections);
            Interlocked.Add(ref collectorTicks, Stopwatch.GetTimestamp() - start);
            return result;
        }
        bool shared = !kind.EndsWith("independent", StringComparison.Ordinal);
        var cache = new WindowSnapshotCache(Collect, TimeSpan.FromMilliseconds(freshness),
            freshnessPolicy: adaptive ? new WindowSnapshotFreshnessPolicy(TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(100)) : null);
        var startSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watch = new Stopwatch();
        var work = Enumerable.Range(0, readers).Select(_ => Task.Run(async () =>
        {
            await startSignal.Task;
            var samples = new List<double>();
            var stability = new WindowStabilityTracker();
            while (watch.ElapsedMilliseconds < (waitForStability ? 5000 : 800))
            {
                long start = Stopwatch.GetTimestamp();
                var snapshot = shared ? await cache.GetSnapshotAsync(maximumAge: adaptive && waitForStability ? stability.MaximumSnapshotAge(500) : null)
                    : new WindowSnapshot(0, Stopwatch.GetTimestamp(), Collect());
                samples.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                if (waitForStability && stability.Observe(snapshot, snapshot.ForProcess(uint.MaxValue), 500)) break;
                await Task.Delay(25);
            }
            return samples;
        })).ToArray();
        watch.Start(); startSignal.SetResult();
        var observations = await Task.WhenAll(work);
        var sorted = observations.SelectMany(s => s).OrderBy(t => t).ToArray();
        return new(kind, readers, injectedDelay, freshness, collections, sorted.Length,
            watch.Elapsed.TotalMilliseconds, sorted[sorted.Length / 2], sorted[(int)Math.Ceiling(sorted.Length * .95) - 1],
            (double)collectorTicks / Stopwatch.Frequency * 1000, cache.CurrentFreshness.TotalMilliseconds);
    }

    private static void Print(SearchRun r) => Console.WriteLine(
        $"{r.Kind}: readers={r.Readers} injected={r.InjectedDelayMs}ms TTL={r.FreshnessMs}ms " +
        $"collections={r.Collections} requests={r.Requests} elapsed={r.ElapsedMs:F1}ms p95request={r.P95RequestMs:F2}ms finalTTL={r.FinalFreshnessMs:F1}ms");
}
