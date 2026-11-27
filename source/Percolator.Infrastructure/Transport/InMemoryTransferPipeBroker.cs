namespace Percolator.Infrastructure.Transport;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Hosting;

/// <summary>
/// Strictly zero-disk, RAM-ring-buffered bidirectional transfer pipe broker.
/// Pairs exactly two callers presenting a matching random 32-byte PipeRendezvousToken,
/// bounds in-flight RAM to ~256 KB per direction with backpressure, enforces concurrency,
/// per-pipe bandwidth, per-session total bytes, and idle timeout quotas, and writes 0 bytes to disk.
/// </summary>
public sealed class InMemoryTransferPipeBroker
{
    public const int MaxFramePayloadBytes = 65_536;
    public const int BoundedChannelFrameCapacity = 4; // 4 * 64 KB = 256 KB per direction

    private readonly object _sync = new();
    private readonly Dictionary<Bytes32Key, PipeSessionSlot> _pending = new();
    private int _activePipeCount;

    public int ActivePipeCount
    {
        get
        {
            lock (_sync)
            {
                return _activePipeCount;
            }
        }
    }

    public int PendingPipeCount
    {
        get
        {
            lock (_sync)
            {
                return _pending.Count;
            }
        }
    }

    internal DomainResult<PipeParticipantHandle> JoinOrCreate(
        ReadOnlySpan<byte> rendezvousToken,
        RelayHostingPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (rendezvousToken.Length != 32)
        {
            return DomainResult<PipeParticipantHandle>.Failure(
                new DomainError("PIPE_INVALID_TOKEN", "PipeRendezvousToken must be exactly 32 bytes."));
        }

        var key = new Bytes32Key(rendezvousToken);

        lock (_sync)
        {
            if (_pending.TryGetValue(key, out var existing))
            {
                _pending.Remove(key);
                existing.MarkPairedLocked();
                return DomainResult<PipeParticipantHandle>.Success(
                    new PipeParticipantHandle(this, key, existing, isInitiator: false));
            }

            if (_activePipeCount >= policy.MaxConcurrentPipes)
            {
                return DomainResult<PipeParticipantHandle>.Failure(
                    new DomainError("PIPE_CONCURRENCY_EXCEEDED", $"Maximum concurrent transfer pipes ({policy.MaxConcurrentPipes}) reached."));
            }

            var slot = new PipeSessionSlot(policy);
            _pending[key] = slot;
            _activePipeCount++;

            return DomainResult<PipeParticipantHandle>.Success(
                new PipeParticipantHandle(this, key, slot, isInitiator: true));
        }
    }

    internal void ReleaseParticipant(Bytes32Key key, PipeSessionSlot slot)
    {
        bool shouldDisposeSlot = false;
        lock (_sync)
        {
            if (!slot.WasPaired && _pending.TryGetValue(key, out var pendingSlot) && ReferenceEquals(pendingSlot, slot))
            {
                _pending.Remove(key);
            }

            if (slot.DecrementParticipantLocked() == 0)
            {
                _activePipeCount = Math.Max(0, _activePipeCount - 1);
                shouldDisposeSlot = true;
            }
        }

        if (shouldDisposeSlot)
        {
            slot.Dispose();
        }
    }
}

internal sealed class PipeParticipantHandle : IDisposable
{
    private readonly InMemoryTransferPipeBroker _broker;
    private readonly Bytes32Key _key;
    private int _disposed;

    public PipeSessionSlot Slot { get; }
    public bool IsInitiator { get; }

    public ChannelWriter<byte[]> OutgoingWriter => IsInitiator ? Slot.AtoB.Writer : Slot.BtoA.Writer;
    public ChannelReader<byte[]> IncomingReader => IsInitiator ? Slot.BtoA.Reader : Slot.AtoB.Reader;

    public PipeParticipantHandle(
        InMemoryTransferPipeBroker broker,
        Bytes32Key key,
        PipeSessionSlot slot,
        bool isInitiator)
    {
        _broker = broker;
        _key = key;
        Slot = slot;
        IsInitiator = isInitiator;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            OutgoingWriter.TryComplete();
            _broker.ReleaseParticipant(_key, Slot);
        }
    }
}

internal sealed class PipeSessionSlot : IDisposable
{
    private readonly long _maxBytesPerSession;
    private readonly long _maxBytesPerSecond;
    private readonly SemaphoreSlim _rateLock = new(1, 1);
    private long _totalBytesForwarded;
    private long _windowStartTimestamp = Stopwatch.GetTimestamp();
    private long _bytesInCurrentWindow;
    private int _remainingParticipants = 1;
    private int _aborted;

    public TimeSpan IdleTimeout { get; }
    public bool WasPaired { get; private set; }
    public TaskCompletionSource<bool> PeerJoinedTcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public CancellationTokenSource SessionCts { get; } = new();
    public Channel<byte[]> AtoB { get; }
    public Channel<byte[]> BtoA { get; }
    public string? AbortReason { get; private set; }

    public PipeSessionSlot(RelayHostingPolicy policy)
    {
        _maxBytesPerSession = policy.MaxBytesPerSession;
        _maxBytesPerSecond = policy.MaxBytesPerSecondPerPipe;
        IdleTimeout = policy.PipeIdleTimeout;

        var channelOptions = new BoundedChannelOptions(InMemoryTransferPipeBroker.BoundedChannelFrameCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        };

        AtoB = Channel.CreateBounded<byte[]>(channelOptions);
        BtoA = Channel.CreateBounded<byte[]>(channelOptions);
    }

    public void MarkPairedLocked()
    {
        WasPaired = true;
        _remainingParticipants++;
        PeerJoinedTcs.TrySetResult(true);
    }

    public int DecrementParticipantLocked()
    {
        _remainingParticipants--;
        return _remainingParticipants;
    }

    public bool TryReserveSessionBytes(int byteCount)
    {
        long updated = Interlocked.Add(ref _totalBytesForwarded, byteCount);
        return updated <= _maxBytesPerSession;
    }

    public async ValueTask ThrottleBandwidthAsync(int byteCount, CancellationToken ct)
    {
        if (byteCount <= 0)
        {
            return;
        }

        await _rateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            long now = Stopwatch.GetTimestamp();
            double elapsedSeconds = Stopwatch.GetElapsedTime(_windowStartTimestamp, now).TotalSeconds;
            if (elapsedSeconds >= 1.0)
            {
                _windowStartTimestamp = now;
                _bytesInCurrentWindow = 0;
            }

            _bytesInCurrentWindow += byteCount;
            if (_bytesInCurrentWindow > _maxBytesPerSecond)
            {
                double overflowSeconds = (_bytesInCurrentWindow - _maxBytesPerSecond) / (double)_maxBytesPerSecond;
                var delay = TimeSpan.FromSeconds(Math.Min(overflowSeconds, 1.0));
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }

                _windowStartTimestamp = Stopwatch.GetTimestamp();
                _bytesInCurrentWindow = 0;
            }
        }
        finally
        {
            _rateLock.Release();
        }
    }

    public void Abort(string reason)
    {
        if (Interlocked.Exchange(ref _aborted, 1) == 0)
        {
            AbortReason = reason;
            AtoB.Writer.TryComplete();
            BtoA.Writer.TryComplete();
            try
            {
                SessionCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    public void Dispose()
    {
        AtoB.Writer.TryComplete();
        BtoA.Writer.TryComplete();
        SessionCts.Dispose();
        _rateLock.Dispose();
    }
}
