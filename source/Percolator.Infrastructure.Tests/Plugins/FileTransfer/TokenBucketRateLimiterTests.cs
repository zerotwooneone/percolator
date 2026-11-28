using System.Diagnostics;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Infrastructure.Plugins.FileTransfer;

namespace Percolator.Infrastructure.Tests.Plugins.FileTransfer;

[TestFixture]
public sealed class TokenBucketRateLimiterTests
{
    private sealed class ControllableTimeProvider : TimeProvider
    {
        private long _timestamp;

        public ControllableTimeProvider(long initialTimestamp = 0)
        {
            _timestamp = initialTimestamp;
        }

        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

        public void Advance(long ticks)
        {
            Interlocked.Add(ref _timestamp, ticks);
        }
    }

    [Test]
    public async Task UT_RLM_01_RemoveSession_EvictsSessionBucketsCleanly()
    {
        ControllableTimeProvider timeProvider = new(Stopwatch.GetTimestamp());
        TokenBucketRateLimiter limiter = new(timeProvider);
        TransferSessionId sessionId = TransferSessionId.New();

        // Configure caps
        await limiter.SetSessionSpeedCapsAsync(
            sessionId,
            BandwidthLimit.Capped(TransferBandwidth.FromBytesPerSecond(1024)),
            BandwidthLimit.Capped(TransferBandwidth.FromBytesPerSecond(2048)));

        // Consume tokens to initialize buckets
        await limiter.ThrottleDownloadAsync(sessionId, TransferByteLength.FromBytes(512));
        await limiter.ThrottleUploadAsync(sessionId, TransferByteLength.FromBytes(512));

        // Remove session
        limiter.RemoveSession(sessionId);

        // Calling RemoveSession again must be safe and idempotent
        Action act = () => limiter.RemoveSession(sessionId);
        act.Should().NotThrow();
    }

    [Test]
    public async Task UT_RLM_02_SplitLocks_DownloadAndUploadDoNotBlockConcurrently()
    {
        ControllableTimeProvider timeProvider = new(Stopwatch.GetTimestamp());
        TokenBucketRateLimiter limiter = new(timeProvider);
        TransferSessionId sessionId = TransferSessionId.New();

        // Download cap is tight (1000 bytes/sec), upload cap is unlimited
        await limiter.SetGlobalSpeedCapsAsync(
            BandwidthLimit.Capped(TransferBandwidth.FromBytesPerSecond(1000)),
            BandwidthLimit.Unlimited);

        // Task 1 requests download of 5000 bytes -> causes deficit and global delay
        Task dlTask = Task.Run(async () =>
        {
            await limiter.ThrottleDownloadAsync(sessionId, TransferByteLength.FromBytes(5000));
        });

        // Give dlTask a moment to enter ThrottleDownloadAsync and acquire download lock
        await Task.Delay(50);

        // Upload should complete IMMEDIATELY without waiting for download delay
        var ulStopwatch = Stopwatch.StartNew();
        await limiter.ThrottleUploadAsync(sessionId, TransferByteLength.FromBytes(5000));
        ulStopwatch.Stop();

        // Upload should not have been blocked by the download throttle
        ulStopwatch.ElapsedMilliseconds.Should().BeLessThan(1000);
    }

    [Test]
    public async Task UT_RLM_03_RemoveSession_ConcurrentThrottleDisposalSafety()
    {
        ControllableTimeProvider timeProvider = new(Stopwatch.GetTimestamp());
        TokenBucketRateLimiter limiter = new(timeProvider);
        TransferSessionId sessionId = TransferSessionId.New();

        await limiter.SetSessionSpeedCapsAsync(
            sessionId,
            BandwidthLimit.Capped(TransferBandwidth.FromBytesPerSecond(100)),
            BandwidthLimit.Capped(TransferBandwidth.FromBytesPerSecond(100)));

        Task task1 = Task.Run(async () =>
        {
            for (int i = 0; i < 20; i++)
            {
                await limiter.ThrottleDownloadAsync(sessionId, TransferByteLength.FromBytes(50));
                await Task.Delay(5);
            }
        });

        Task task2 = Task.Run(async () =>
        {
            await Task.Delay(10);
            limiter.RemoveSession(sessionId);
        });

        Func<Task> act = async () => await Task.WhenAll(task1, task2);
        await act.Should().NotThrowAsync();
    }
}
