namespace Common.Utils.VDL.OS;

/// <summary>Amortize expensive collection without letting reader count drive the scan rate.</summary>
internal sealed class WindowSnapshotFreshnessPolicy
{
    private readonly TimeSpan _minimum;
    private readonly TimeSpan _maximum;
    private double? _averageCollectionMs;
    private long _currentTicks;

    public WindowSnapshotFreshnessPolicy(TimeSpan minimum, TimeSpan maximum)
    {
        if (minimum <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(minimum));
        if (maximum < minimum) throw new ArgumentOutOfRangeException(nameof(maximum));
        _minimum = minimum;
        _maximum = maximum;
        _currentTicks = minimum.Ticks;
    }

    public TimeSpan CurrentFreshness => TimeSpan.FromTicks(Volatile.Read(ref _currentTicks));

    // Called only by the cache's serialized collector. Readers only access CurrentFreshness.
    public void RecordCollection(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(elapsed));
        double cost = elapsed.TotalMilliseconds;
        _averageCollectionMs = _averageCollectionMs is double previous
            ? previous * .75 + cost * .25 : cost;
        // Aim for three units of reuse time per unit of collection wall time.
        // This is a workload throttle, not a measurement of CPU utilization.
        double milliseconds = Math.Clamp(_averageCollectionMs.Value * 3,
            _minimum.TotalMilliseconds, _maximum.TotalMilliseconds);
        Volatile.Write(ref _currentTicks, TimeSpan.FromMilliseconds(milliseconds).Ticks);
    }
}
