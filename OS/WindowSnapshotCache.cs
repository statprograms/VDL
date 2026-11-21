using System.Collections.Frozen;

namespace Common.Utils.VDL.OS;

/// <summary>One immutable collection pass, shared by all readers.</summary>
internal sealed class WindowSnapshot
{
    public long Version { get; }
    public long CapturedAt { get; }
    public IReadOnlyList<WData> Windows { get; }
    public IReadOnlyDictionary<uint, IReadOnlyList<WData>> WindowsByPid { get; }
    public IReadOnlyDictionary<IntPtr, WData> WindowsByHandle { get; }

    public WindowSnapshot(long version, long capturedAt, IEnumerable<WData> windows)
    {
        Version = version;
        CapturedAt = capturedAt;
        var collected = windows.ToArray();
        Windows = Array.AsReadOnly(collected);
        WindowsByPid = collected.GroupBy(w => w.PID).ToFrozenDictionary(
            g => g.Key, g => (IReadOnlyList<WData>)Array.AsReadOnly(g.ToArray()));
        WindowsByHandle = collected.ToFrozenDictionary(w => w.Handle);
    }

    public IReadOnlyList<WData> ForProcess(uint pid)
        => WindowsByPid.TryGetValue(pid, out var windows) ? windows : Array.Empty<WData>();
}

/// <summary>On-demand fresh reads: one collector at a time, with no background timer.</summary>
internal sealed class WindowSnapshotCache
{
    private sealed record Entry(WindowSnapshot Snapshot, long Invalidation);

    private readonly Func<IEnumerable<WData>> _collect;
    private readonly TimeSpan _freshness;
    private readonly TimeProvider _clock;
    private readonly WindowSnapshotFreshnessPolicy? _freshnessPolicy;
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private Entry? _entry;
    private long _invalidation;
    private long _version;

    public WindowSnapshotCache(Func<IEnumerable<WData>> collect, TimeSpan freshness, TimeProvider? clock = null,
        WindowSnapshotFreshnessPolicy? freshnessPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(collect);
        if (freshness <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(freshness));
        _collect = collect;
        _freshness = freshness;
        _clock = clock ?? TimeProvider.System;
        _freshnessPolicy = freshnessPolicy;
    }

    public TimeSpan CurrentFreshness => _freshnessPolicy?.CurrentFreshness ?? _freshness;

    public WindowSnapshot GetSnapshot(TimeSpan? maximumAge = null)
        => GetSnapshotAsync(maximumAge: maximumAge).GetAwaiter().GetResult();

    public async ValueTask<WindowSnapshot> GetSnapshotAsync(CancellationToken ct = default, TimeSpan? maximumAge = null)
    {
        if (maximumAge <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maximumAge));
        ct.ThrowIfCancellationRequested();
        var entry = Volatile.Read(ref _entry);
        if (IsFresh(entry, maximumAge)) return entry!.Snapshot;

        await _refresh.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                entry = Volatile.Read(ref _entry);
                if (IsFresh(entry, maximumAge)) return entry!.Snapshot;

                long invalidation = Volatile.Read(ref _invalidation);
                long collectionStarted = _clock.GetTimestamp();
                var windows = _collect().ToArray();
                // An operation may have changed windows while collection was running.
                if (invalidation != Volatile.Read(ref _invalidation)) continue;

                var snapshot = new WindowSnapshot(++_version, _clock.GetTimestamp(), windows);
                _freshnessPolicy?.RecordCollection(_clock.GetElapsedTime(collectionStarted, _clock.GetTimestamp()));
                Volatile.Write(ref _entry, new Entry(snapshot, invalidation));
                ct.ThrowIfCancellationRequested();
                return snapshot;
            }
        }
        finally
        {
            _refresh.Release();
        }
    }

    private bool IsFresh(Entry? entry, TimeSpan? maximumAge)
        => entry != null && entry.Invalidation == Volatile.Read(ref _invalidation)
            && _clock.GetElapsedTime(entry.Snapshot.CapturedAt, _clock.GetTimestamp()) <
                (maximumAge is TimeSpan limit && limit < CurrentFreshness ? limit : CurrentFreshness);

    public void Invalidate() => Interlocked.Increment(ref _invalidation);
}
