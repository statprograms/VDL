using System.Diagnostics;

namespace Common.Utils.VDL.Data;

// Example of use with the synchronous wrapper
/*

Func<CancellationToken, Task<MyData>> factoryWrapper = ct => 
{
    // Offload the heavy blocking synchronous method to a background ThreadPool thread
    return Task.Run(() => MyHeavyBlockingSyncFactoryMethod(), ct);
};

var cache = new SimpleThresholdCache<MyData>(factoryWrapper, TimeSpan.FromMinutes(5));
*/

public enum CacheStaleStrategy
{
	/// <summary>
	/// Triggers an active background refresh task. Callers arriving during the 
	/// update window will await this task, exposing them to any exceptions thrown by the 
	/// underlying provider.
	/// </summary>
	ReturnStaleImmediately,

	/// <summary>
	/// Forces callers to await the active asynchronous update task until fresh 
	/// data arrives.
	/// </summary>
	WaitForFreshData
}

public class SimpleThresholdCache<T> : IDisposable
{
	private readonly Func<CancellationToken, Task<T>> _updateFactory;
	private readonly CacheStaleStrategy _staleStrategy;
	private readonly SemaphoreSlim _lock = new(1, 1);
	private volatile Task<T> _cachedTask;
	private long _lastUpdateTimestamp;
	private long _thresholdTicks;
	private bool _isDisposed;

	// Checks if the time elapsed since the last successful timestamp exceeds the threshold
	protected bool IsStale => Stopwatch.GetElapsedTime(Volatile.Read(ref _lastUpdateTimestamp)).Ticks > Volatile.Read(ref _thresholdTicks);

	public SimpleThresholdCache(
		Func<CancellationToken, Task<T>> updateFactory,
		TimeSpan threshold,
		CacheStaleStrategy staleStrategy = CacheStaleStrategy.ReturnStaleImmediately)
	{
		_updateFactory = updateFactory ?? throw new ArgumentNullException(nameof(updateFactory));
		_thresholdTicks = threshold.Ticks;	
		_staleStrategy = staleStrategy;

		// will be updated on the very first call
		_cachedTask = Task.FromException<T>(new InvalidOperationException("Uninitialized"));
	}

	/// <summary>
	/// Gets the cached value asynchronously. This is the preferred non-blocking entry point.
	/// </summary>
	public async Task<T> GetValueAsync(CancellationToken ct = default)
	{
		ObjectDisposedException.ThrowIf(_isDisposed, this);

		Task<T> currentTask = _cachedTask;

		if (currentTask.IsCompletedSuccessfully && !IsStale)
		{
			return await currentTask.ConfigureAwait(false);
		}

		await _lock.WaitAsync(ct).ConfigureAwait(false);
		bool triggeredRefresh = false;
		try
		{
			if (_cachedTask == currentTask)
			{
				if (IsStale || !_cachedTask.IsCompletedSuccessfully)
				{
					_cachedTask = ExecuteAndTimestampRefreshAsync(CancellationToken.None);
					triggeredRefresh = true;
				}
			}
		}
		finally
		{
			_lock.Release();
		}

		// If we want fresh data, OR the entry task was broken, we MUST await the latest global task
		if (_staleStrategy == CacheStaleStrategy.WaitForFreshData || !currentTask.IsCompletedSuccessfully)
		{
			return await _cachedTask.ConfigureAwait(false);
		}

		// ReturnStaleImmediately Strategy:
		return await (triggeredRefresh ? currentTask : _cachedTask).ConfigureAwait(false);
	}

	/// <summary>
	/// Refreshes cycle immediately.
	/// </summary>
	public async Task<T> RefreshAsync(CancellationToken ct = default)
	{
		ObjectDisposedException.ThrowIf(_isDisposed, this);

		await _lock.WaitAsync(ct).ConfigureAwait(false);
		try
		{
			_cachedTask = ExecuteAndTimestampRefreshAsync(ct);
		}
		finally
		{
			_lock.Release();
		}

		return await _cachedTask.ConfigureAwait(false);
	}

	private async Task<T> ExecuteAndTimestampRefreshAsync(CancellationToken ct)
	{
		try
		{
			T freshValue = await _updateFactory(ct).ConfigureAwait(false);
			Volatile.Write(ref _lastUpdateTimestamp, Stopwatch.GetTimestamp());
			return freshValue;
		}
		catch
		{
			// The next call immediately tries to self-heal
			Volatile.Write(ref _lastUpdateTimestamp, 0);
			throw;
		}
	}

	public void UpdateThreshold(TimeSpan newThreshold)
	{
		Volatile.Write(ref _thresholdTicks, newThreshold.Ticks);
	}

	public void Dispose()
	{
		_isDisposed = true;
		_lock.Dispose();
	}
}
