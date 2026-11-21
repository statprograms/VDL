using Common.Utils.VDL.OS;
using VDL;

internal static class WindowSnapshotTests
{
    public static async Task<int> RunAsync()
    {
        int checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("FAILED: " + name);
            checks++;
        }
        var clock = new ManualClock();
        var original = new WData { PID = 1, Handle = new IntPtr(10), IsMain = true, Title = "Original" };
        int collections = 0;
        var cache = new WindowSnapshotCache(() =>
        {
            collections++;
            return new[] { original };
        }, TimeSpan.FromMilliseconds(25), clock);

        var first = cache.GetSnapshot();
        Check(first.Version == 1 && first.CapturedAt == 0, "snapshot has a version and monotonic capture time");
        Check(ReferenceEquals(first, await cache.GetSnapshotAsync()) && collections == 1, "fresh callers share the same snapshot");
        Check(first.ForProcess(1).Single() == original && first.WindowsByHandle[original.Handle] == original,
            "snapshot indexes windows by PID and handle");
        Check(first.ForProcess(99).Count == 0, "absent PID returns an empty collection");
        clock.Advance(24);
        Check(ReferenceEquals(first, cache.GetSnapshot()), "snapshot reused before expiry");
        clock.Advance(1);
        var second = cache.GetSnapshot();
        Check(second.Version == 2 && collections == 2 && second.CapturedAt > first.CapturedAt, "exact TTL boundary refreshes");
        cache.Invalidate();
        Check(cache.GetSnapshot().Version == 3 && collections == 3, "invalidation forces refresh before TTL expiry");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try { await cache.GetSnapshotAsync(cancelled.Token); throw new Exception("Cancelled reader succeeded"); }
            catch (OperationCanceledException) { checks++; }
        }
        Check(collections == 3, "cancelled reader does not collect");

        // Freeze the collector so all concurrent readers encounter the same refresh.
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int concurrentCollections = 0;
        var concurrent = new WindowSnapshotCache(() =>
        {
            Interlocked.Increment(ref concurrentCollections);
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test collector was not released");
            return new[] { original };
        }, TimeSpan.FromMilliseconds(25), clock);
        var owner = Task.Run(() => concurrent.GetSnapshot());
        if (!entered.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test collector did not start");
        var readers = Enumerable.Range(0, 12).Select(_ => concurrent.GetSnapshotAsync().AsTask()).ToArray();
        using var waitingCancellation = new CancellationTokenSource();
        var cancelledWaiter = concurrent.GetSnapshotAsync(waitingCancellation.Token).AsTask();
        waitingCancellation.Cancel();
        try
        {
            try { await cancelledWaiter; throw new Exception("Cancelled waiter succeeded"); }
            catch (OperationCanceledException) { checks++; }
        }
        finally { release.Set(); }
        var shared = await owner;
        var results = await Task.WhenAll(readers);
        Check(concurrentCollections == 1 && results.All(s => ReferenceEquals(s, shared)), "concurrent readers collect exactly once");
        Check(ReferenceEquals(shared, await concurrent.GetSnapshotAsync()), "waiter cancellation does not cancel shared refresh");

        // A window mutation during collection must prevent that pass from being published.
        WindowSnapshotCache? invalidatedDuringCollection = null;
        int invalidatedCollections = 0;
        invalidatedDuringCollection = new WindowSnapshotCache(() =>
        {
            if (++invalidatedCollections == 1)
            {
                invalidatedDuringCollection!.Invalidate();
                return new[] { original };
            }
            return Array.Empty<WData>();
        }, TimeSpan.FromMilliseconds(25), clock);
        Check(invalidatedDuringCollection.GetSnapshot().Windows.Count == 0 && invalidatedCollections == 2,
            "collection invalidated in flight is discarded and repeated");

        int failureCollections = 0;
        var failures = new WindowSnapshotCache(() =>
        {
            if (++failureCollections == 2) throw new InvalidOperationException("Simulated collection failure");
            return failureCollections == 1 ? new[] { original } : Array.Empty<WData>();
        }, TimeSpan.FromMilliseconds(25), clock);
        var beforeFailure = failures.GetSnapshot();
        clock.Advance(25);
        try { failures.GetSnapshot(); throw new Exception("Refresh failure was swallowed"); }
        catch (InvalidOperationException) { checks++; }
        var recovered = failures.GetSnapshot();
        Check(recovered.Version == 2 && recovered.Windows.Count == 0 && failureCollections == 3, "failed refresh can be retried without publishing partial data");
        Check(beforeFailure.ForProcess(1).Single().Title == "Original", "previous snapshots remain unchanged after refresh");
        try { ((IList<WData>)first.Windows).Add(original); throw new Exception("Snapshot was mutable"); }
        catch (NotSupportedException) { checks++; }
        try { ((IList<WData>)first.ForProcess(1)).Clear(); throw new Exception("PID index was mutable"); }
        catch (NotSupportedException) { checks++; }

        WindowSnapshot Sample(long version, int milliseconds, params WData[] windows)
            => new(version, TimeSpan.FromMilliseconds(milliseconds).Ticks, windows);
        var state = new WindowStabilityTracker(clock);
        var start = Sample(1, 0, original);
        Check(!state.Observe(start, start.ForProcess(1), 500), "first observation starts stability");
        clock.Advance(1000);
        Check(!state.Observe(start, start.ForProcess(1), 500), "repeated cached read cannot manufacture stability from wall time");
        var early = Sample(2, 400, original);
        Check(!state.Observe(early, early.ForProcess(1), 500), "stability uses observation timestamps instead of current clock");
        var stable = Sample(3, 500, original);
        Check(state.Observe(stable, stable.ForProcess(1), 500), "fresh observation at stability deadline succeeds");

        var disappeared = new WindowStabilityTracker(clock);
        Check(!disappeared.Observe(start, start.ForProcess(1), 500), "disappearance scenario begins unstable");
        Check(!disappeared.Observe(Sample(2, 250), Array.Empty<WData>(), 500), "missing window resets stability");
        var returned = Sample(3, 400, original);
        Check(!disappeared.Observe(returned, returned.ForProcess(1), 500), "returning window starts a new interval");
        Check(!disappeared.Observe(Sample(4, 800, original), new[] { original }, 500), "old interval does not carry over after disappearance");
        Check(disappeared.Observe(Sample(5, 900, original), new[] { original }, 500), "returning window stabilizes after its own interval");

        var another = new WData { PID = 1, Handle = new IntPtr(11), IsMain = true };
        var changed = new WindowStabilityTracker(clock);
        changed.Observe(start, new[] { original }, 500);
        Check(!changed.Observe(Sample(2, 450, original, another), new[] { original, another }, 500), "matching count change resets stability");
        Check(!changed.Observe(Sample(3, 700, original, another), new[] { original, another }, 500), "count change requires a full new interval");
        Check(changed.Observe(Sample(4, 950, original, another), new[] { original, another }, 500), "unchanged new count eventually stabilizes");
        Check(!changed.Observe(Sample(5, 1000, another, original), new[] { another, original }, 500), "best-handle change also resets stability");
        Check(!changed.Observe(Sample(4, 2000, another), new[] { another }, 500), "older snapshot versions are ignored");

        var splash = new WData { PID = 1, Handle = new IntPtr(12), IsMain = false };
        var nonMain = new WindowStabilityTracker(clock);
        nonMain.Observe(Sample(1, 0, splash), new[] { splash }, 500);
        Check(!nonMain.Observe(Sample(2, 500, splash), new[] { splash }, 500), "non-main candidate requires 50 percent longer stability");
        Check(nonMain.Observe(Sample(3, 750, splash), new[] { splash }, 500), "non-main candidate stabilizes at 750ms by default");
        var zero = new WindowStabilityTracker(clock);
        Check(!zero.Observe(start, new[] { original }, 0), "zero stability still starts with an initial observation");
        Check(zero.Observe(Sample(2, 1, original), new[] { original }, 0), "zero stability accepts the next fresh unchanged observation");
        var policy = new WindowSnapshotFreshnessPolicy(TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(100));
        Check(policy.CurrentFreshness.TotalMilliseconds == 25, "adaptive policy starts with minimum freshness interval");
        policy.RecordCollection(TimeSpan.FromMilliseconds(1));
        Check(policy.CurrentFreshness.TotalMilliseconds == 25, "cheap collection retains 25ms interval");
        var expensivePolicy = new WindowSnapshotFreshnessPolicy(TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(100));
        expensivePolicy.RecordCollection(TimeSpan.FromMilliseconds(20));
        Check(expensivePolicy.CurrentFreshness.TotalMilliseconds == 60, "slow collection increases reuse interval based on cost");
        expensivePolicy.RecordCollection(TimeSpan.FromMilliseconds(100));
        Check(expensivePolicy.CurrentFreshness.TotalMilliseconds == 100, "reuse interval never exceeds upper bound");
        for (int i = 0; i < 20; i++) expensivePolicy.RecordCollection(TimeSpan.FromMilliseconds(1));
        Check(expensivePolicy.CurrentFreshness.TotalMilliseconds == 25, "policy returns to minimum after collection becomes cheap");
        var smoothed = new WindowSnapshotFreshnessPolicy(TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(100));
        smoothed.RecordCollection(TimeSpan.FromMilliseconds(10));
        smoothed.RecordCollection(TimeSpan.FromMilliseconds(30));
        Check(smoothed.CurrentFreshness.TotalMilliseconds == 45, "cost smoothing damps a single expensive observation");

        var adaptiveClock = new ManualClock();
        int adaptiveCollections = 0;
        var adaptiveCache = new WindowSnapshotCache(() =>
        {
            adaptiveCollections++; adaptiveClock.Advance(40);
            return new[] { original };
        }, TimeSpan.FromMilliseconds(25), adaptiveClock,
            new WindowSnapshotFreshnessPolicy(TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(100)));
        var adaptiveFirst = adaptiveCache.GetSnapshot();
        Check(adaptiveCache.CurrentFreshness.TotalMilliseconds == 100, "cache measures collection and updates policy");
        adaptiveClock.Advance(30);
        Check(ReferenceEquals(adaptiveFirst, adaptiveCache.GetSnapshot()), "adaptive cache reuses older snapshot within selected interval");
        var demanding = adaptiveCache.GetSnapshot(TimeSpan.FromMilliseconds(25));
        Check(demanding.Version > adaptiveFirst.Version && adaptiveCollections == 2, "caller freshness requirement overrides adaptive interval");
        Check(adaptiveCache.CurrentFreshness.TotalMilliseconds == 100, "one caller freshness cap does not globally retune policy");
        adaptiveCache.Invalidate();
        Check(adaptiveCache.GetSnapshot().Version > demanding.Version && adaptiveCollections == 3, "explicit invalidation bypasses adaptive lifetime");
        adaptiveClock.Advance(100);
        Check(adaptiveCache.GetSnapshot().Version == 4, "adaptive cache expires at its chosen boundary");
        try { adaptiveCache.GetSnapshot(TimeSpan.Zero); throw new Exception("Zero maximum age accepted"); }
        catch (ArgumentOutOfRangeException) { checks++; }

        var deadlineClock = new ManualClock();
        var deadline = new WindowStabilityTracker(deadlineClock);
        Check(deadline.MaximumSnapshotAge(500).TotalMilliseconds == 25, "initial observation requires baseline freshness");
        deadline.Observe(start, new[] { original }, 500);
        Check(deadline.MaximumSnapshotAge(500).TotalMilliseconds == 100, "long remaining stability interval permits bounded reuse");
        deadlineClock.Advance(450);
        Check(deadline.MaximumSnapshotAge(500).TotalMilliseconds == 50, "freshness tightens as deadline approaches");
        deadlineClock.Advance(40);
        Check(deadline.MaximumSnapshotAge(500).TotalMilliseconds == 25, "near-deadline cap preserves original polling resolution");
        deadlineClock.Advance(100);
        Check(deadline.MaximumSnapshotAge(500).TotalMilliseconds == 25, "elapsed deadline cannot leave a long adaptive freshness interval");
        Check(deadline.MaximumSnapshotAge(0).TotalMilliseconds == 25, "zero stability does not inherit long freshness interval");
        var shortWait = new WindowStabilityTracker(new ManualClock());
        shortWait.Observe(start, new[] { original }, 20);
        Check(shortWait.MaximumSnapshotAge(20).TotalMilliseconds == 25, "short stability keeps original 25ms freshness");
        var nonMainClock = new ManualClock();
        var nonMainDeadline = new WindowStabilityTracker(nonMainClock);
        nonMainDeadline.Observe(Sample(1, 0, splash), new[] { splash }, 500);
        nonMainClock.Advance(680);
        Check(nonMainDeadline.MaximumSnapshotAge(500).TotalMilliseconds == 70, "deadline calculation includes non-main stability multiplier");
        return checks;
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
        public void Advance(int milliseconds) => Interlocked.Add(ref _timestamp, TimeSpan.FromMilliseconds(milliseconds).Ticks);
    }
}
