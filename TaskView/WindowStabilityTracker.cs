namespace VDL;

/// <summary>Advance stability only when a newer snapshot supplies another observation.</summary>
internal sealed class WindowStabilityTracker(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private long _lastVersion = -1;
    private IntPtr _lastBestHandle;
    private int _lastCount = -1;
    private long? _unchangedSince;
    private bool _lastWasMain;

    public TimeSpan MaximumSnapshotAge(int stabilityMilliseconds)
    {
        if (!_unchangedSince.HasValue) return SystemWindows.SnapshotFreshness;
        double required = _lastWasMain ? stabilityMilliseconds : stabilityMilliseconds * 1.5;
        double remaining = required - _clock.GetElapsedTime(_unchangedSince.Value, _clock.GetTimestamp()).TotalMilliseconds;
        return TimeSpan.FromMilliseconds(Math.Clamp(remaining,
            SystemWindows.SnapshotFreshness.TotalMilliseconds, SystemWindows.MaximumSnapshotFreshness.TotalMilliseconds));
    }

    public bool Observe(WindowSnapshot snapshot, IReadOnlyList<WData> orderedMatches, int stabilityMilliseconds)
    {
        if (snapshot.Version <= _lastVersion) return false;
        _lastVersion = snapshot.Version;

        var best = orderedMatches.FirstOrDefault();
        if (best == null)
        {
            _lastBestHandle = IntPtr.Zero;
            _lastCount = -1;
            _unchangedSince = null;
            return false;
        }
        _lastWasMain = best.IsMain;

        if (best.Handle != _lastBestHandle || orderedMatches.Count != _lastCount)
        {
            _lastBestHandle = best.Handle;
            _lastCount = orderedMatches.Count;
            _unchangedSince = snapshot.CapturedAt;
            return false;
        }

        double requiredMilliseconds = best.IsMain ? stabilityMilliseconds : stabilityMilliseconds * 1.5;
        return _unchangedSince.HasValue
            && _clock.GetElapsedTime(_unchangedSince.Value, snapshot.CapturedAt).TotalMilliseconds >= requiredMilliseconds;
    }
}
