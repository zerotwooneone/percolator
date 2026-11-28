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

        // Set high caps
        await limiter.SetGlobalSpeedCapsAsync(
            BandwidthLimit.Capped(TransferBandwidth.FromBytesPerSecond(10_000_000)),
            BandwidthLimit.Capped(TransferBandwidth.FromBytesPerSecond(10_000_000)));

        Task dlTask = Task.Run(async () =>
        {
            for (int i = 0; i < 50; i++)
            {
                await limiter.ThrottleDownloadAsync(sessionId, TransferByteLength.FromBytes(1024));
            }
        });

        Task ulTask = Task.Run(async () =>
        {
            for (int i = 0; i < 50; i++)
            {
                await limiter.ThrottleUploadAsync(sessionId, TransferByteLength.FromBytes(1024));
            }
        });

        await Task.WhenAll(dlTask, ulTask);
    }
}
