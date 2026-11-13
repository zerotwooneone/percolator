namespace Percolator.Infrastructure.Plugins.FileTransfer;

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Apps.FileTransfer.Ports;

public sealed class TokenBucketRateLimiter : ITransferRateLimiter
{
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    private BandwidthLimit _globalDownloadCap = BandwidthLimit.Unlimited;
    private BandwidthLimit _globalUploadCap = BandwidthLimit.Unlimited;

    private readonly ConcurrentDictionary<TransferSessionId, (BandwidthLimit Download, BandwidthLimit Upload)> _sessionCaps = new();

    private double _globalDownloadTokens;
    private double _globalUploadTokens;
    private long _lastGlobalDownloadTimestamp;
    private long _lastGlobalUploadTimestamp;

    private readonly ConcurrentDictionary<TransferSessionId, TokenBucketState> _sessionDownloadBuckets = new();
    private readonly ConcurrentDictionary<TransferSessionId, TokenBucketState> _sessionUploadBuckets = new();

    public TokenBucketRateLimiter(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        long now = _timeProvider.GetTimestamp();
        _lastGlobalDownloadTimestamp = now;
        _lastGlobalUploadTimestamp = now;
    }

    public ValueTask SetGlobalSpeedCapsAsync(
        BandwidthLimit downloadCap,
        BandwidthLimit uploadCap,
        CancellationToken ct = default)
    {
        _globalDownloadCap = downloadCap;
        _globalUploadCap = uploadCap;
        return ValueTask.CompletedTask;
    }

    public ValueTask SetSessionSpeedCapsAsync(
        TransferSessionId sessionId,
        BandwidthLimit downloadCap,
        BandwidthLimit uploadCap,
        CancellationToken ct = default)
    {
        _sessionCaps[sessionId] = (downloadCap, uploadCap);
        return ValueTask.CompletedTask;
    }

    public async ValueTask ThrottleDownloadAsync(
        TransferSessionId sessionId,
        TransferByteLength byteCount,
        CancellationToken ct = default)
    {
        if (byteCount.Bytes <= 0)
        {
            return;
        }

        // 1. Throttle against session cap if present
        if (_sessionCaps.TryGetValue(sessionId, out var caps) && !caps.Download.IsUnlimited)
        {
            TokenBucketState sessionBucket = _sessionDownloadBuckets.GetOrAdd(
                sessionId,
                _ => new TokenBucketState(_timeProvider.GetTimestamp()));

            await ThrottleBucketAsync(sessionBucket, caps.Download.Value.BytesPerSecond, byteCount.Bytes, ct).ConfigureAwait(false);
        }

        // 2. Throttle against global download cap
        if (!_globalDownloadCap.IsUnlimited)
        {
            TimeSpan globalDelay = TimeSpan.Zero;
            await _syncLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                long now = _timeProvider.GetTimestamp();
                double elapsedSeconds = _timeProvider.GetElapsedTime(_lastGlobalDownloadTimestamp, now).TotalSeconds;
                _lastGlobalDownloadTimestamp = now;

                long cap = _globalDownloadCap.Value.BytesPerSecond;
                _globalDownloadTokens = Math.Min(cap, _globalDownloadTokens + (elapsedSeconds * cap));

                if (_globalDownloadTokens < byteCount.Bytes)
                {
                    double deficit = byteCount.Bytes - _globalDownloadTokens;
                    globalDelay = TimeSpan.FromSeconds(deficit / cap);
                    _globalDownloadTokens = 0;
                }
                else
                {
                    _globalDownloadTokens -= byteCount.Bytes;
                }
            }
            finally
            {
                _syncLock.Release();
            }

            if (globalDelay > TimeSpan.Zero)
            {
                await Task.Delay(globalDelay, _timeProvider, ct).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask ThrottleUploadAsync(
        TransferSessionId sessionId,
        TransferByteLength byteCount,
        CancellationToken ct = default)
    {
        if (byteCount.Bytes <= 0)
        {
            return;
        }

        // 1. Throttle against session cap if present
        if (_sessionCaps.TryGetValue(sessionId, out var caps) && !caps.Upload.IsUnlimited)
        {
            TokenBucketState sessionBucket = _sessionUploadBuckets.GetOrAdd(
                sessionId,
                _ => new TokenBucketState(_timeProvider.GetTimestamp()));

            await ThrottleBucketAsync(sessionBucket, caps.Upload.Value.BytesPerSecond, byteCount.Bytes, ct).ConfigureAwait(false);
        }

        // 2. Throttle against global upload cap
        if (!_globalUploadCap.IsUnlimited)
        {
            TimeSpan globalDelay = TimeSpan.Zero;
            await _syncLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                long now = _timeProvider.GetTimestamp();
                double elapsedSeconds = _timeProvider.GetElapsedTime(_lastGlobalUploadTimestamp, now).TotalSeconds;
                _lastGlobalUploadTimestamp = now;

                long cap = _globalUploadCap.Value.BytesPerSecond;
                _globalUploadTokens = Math.Min(cap, _globalUploadTokens + (elapsedSeconds * cap));

                if (_globalUploadTokens < byteCount.Bytes)
                {
                    double deficit = byteCount.Bytes - _globalUploadTokens;
                    globalDelay = TimeSpan.FromSeconds(deficit / cap);
                    _globalUploadTokens = 0;
                }
                else
                {
                    _globalUploadTokens -= byteCount.Bytes;
                }
            }
            finally
            {
                _syncLock.Release();
            }

            if (globalDelay > TimeSpan.Zero)
            {
                await Task.Delay(globalDelay, _timeProvider, ct).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask ThrottleBucketAsync(
        TokenBucketState bucket,
        long bytesPerSecond,
        long requestedBytes,
        CancellationToken ct)
    {
        TimeSpan delay = TimeSpan.Zero;
        await bucket.Lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            long now = _timeProvider.GetTimestamp();
            double elapsedSeconds = _timeProvider.GetElapsedTime(bucket.LastTimestamp, now).TotalSeconds;
            bucket.LastTimestamp = now;

            bucket.AvailableTokens = Math.Min(bytesPerSecond, bucket.AvailableTokens + (elapsedSeconds * bytesPerSecond));

            if (bucket.AvailableTokens < requestedBytes)
            {
                double deficit = requestedBytes - bucket.AvailableTokens;
                delay = TimeSpan.FromSeconds(deficit / bytesPerSecond);
                bucket.AvailableTokens = 0;
            }
            else
            {
                bucket.AvailableTokens -= requestedBytes;
            }
        }
        finally
        {
            bucket.Lock.Release();
        }

        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, _timeProvider, ct).ConfigureAwait(false);
        }
    }

    private sealed class TokenBucketState
    {
        public SemaphoreSlim Lock { get; } = new(1, 1);
        public double AvailableTokens { get; set; }
        public long LastTimestamp { get; set; }

        public TokenBucketState(long initialTimestamp)
        {
            LastTimestamp = initialTimestamp;
        }
    }
}
