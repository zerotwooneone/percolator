using System;
using System.Collections.Generic;
using System.Threading;

namespace Percolator.Simulator.Features.Shell;

/// <summary>
/// Thread-safe rate limiter that executes the first event immediately (leading edge, 0ms latency)
/// and coalesces high-frequency burst events into a single trailing execution at the end of the window (e.g. 33ms / 30 Hz).
/// When quiet, cooldown timer is inactive and CPU consumption is zero.
/// </summary>
internal sealed class CoalescedActionGate : IDisposable
{
    private readonly TimeSpan _window;
    private readonly Action _action;
    private readonly object _lock = new();
    private readonly Timer _timer;
    private bool _inCooldown;
    private bool _hasPending;
    private bool _disposed;

    public CoalescedActionGate(TimeSpan window, Action action)
    {
        _window = window;
        _action = action ?? throw new ArgumentNullException(nameof(action));
        _timer = new Timer(OnCooldownElapsed, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// Invokes the action immediately if idle, or flags a trailing execution if already in cooldown.
    /// </summary>
    public void RequestExecution()
    {
        bool executeImmediate = false;

        lock (_lock)
        {
            if (_disposed) return;

            if (!_inCooldown)
            {
                // Leading edge: execute immediately
                _inCooldown = true;
                executeImmediate = true;
                _timer.Change((int)_window.TotalMilliseconds, Timeout.Infinite);
            }
            else
            {
                // In cooldown: coalesce burst into trailing edge
                _hasPending = true;
            }
        }

        if (executeImmediate)
        {
            _action();
        }
    }

    private void OnCooldownElapsed(object? state)
    {
        bool executeTrailing = false;

        lock (_lock)
        {
            if (_disposed) return;

            if (_hasPending)
            {
                _hasPending = false;
                executeTrailing = true;
                // Restart cooldown window for further trailing bursts
                _timer.Change((int)_window.TotalMilliseconds, Timeout.Infinite);
            }
            else
            {
                // Cooldown elapsed with no pending burst, return to idle
                _inCooldown = false;
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }

        if (executeTrailing)
        {
            _action();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _hasPending = false;
            _timer.Dispose();
        }
    }
}

/// <summary>
/// Thread-safe accumulator that batches high-frequency items into single flushes at a maximum rate (e.g. 33ms / 30 Hz).
/// Dispatches the first item immediately on the leading edge, and batches subsequent burst items.
/// </summary>
internal sealed class BatchAccumulator<T> : IDisposable
{
    private readonly TimeSpan _window;
    private readonly int _maxBatchSize;
    private readonly Action<List<T>> _flushAction;
    private readonly object _lock = new();
    private readonly List<T> _buffer = new();
    private readonly Timer _timer;
    private bool _inCooldown;
    private bool _disposed;

    public BatchAccumulator(TimeSpan window, int maxBatchSize, Action<List<T>> flushAction)
    {
        _window = window;
        _maxBatchSize = maxBatchSize;
        _flushAction = flushAction ?? throw new ArgumentNullException(nameof(flushAction));
        _timer = new Timer(OnCooldownElapsed, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Enqueue(T item)
    {
        List<T>? immediateFlush = null;

        lock (_lock)
        {
            if (_disposed) return;

            if (!_inCooldown)
            {
                // Leading edge: dispatch first item immediately
                _inCooldown = true;
                immediateFlush = new List<T> { item };
                _timer.Change((int)_window.TotalMilliseconds, Timeout.Infinite);
            }
            else
            {
                _buffer.Add(item);
                if (_buffer.Count >= _maxBatchSize)
                {
                    immediateFlush = new List<T>(_buffer);
                    _buffer.Clear();
                    _timer.Change((int)_window.TotalMilliseconds, Timeout.Infinite);
                }
            }
        }

        if (immediateFlush != null && immediateFlush.Count > 0)
        {
            _flushAction(immediateFlush);
        }
    }

    private void OnCooldownElapsed(object? state)
    {
        List<T>? trailingFlush = null;

        lock (_lock)
        {
            if (_disposed) return;

            if (_buffer.Count > 0)
            {
                trailingFlush = new List<T>(_buffer);
                _buffer.Clear();
                _timer.Change((int)_window.TotalMilliseconds, Timeout.Infinite);
            }
            else
            {
                _inCooldown = false;
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }

        if (trailingFlush != null && trailingFlush.Count > 0)
        {
            _flushAction(trailingFlush);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _buffer.Clear();
            _timer.Dispose();
        }
    }
}
