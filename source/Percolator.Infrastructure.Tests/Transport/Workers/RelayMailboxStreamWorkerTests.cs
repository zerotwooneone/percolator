namespace Percolator.Infrastructure.Tests.Transport.Workers;

using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Infrastructure.Transport.Workers;

[TestFixture]
public sealed class RelayMailboxStreamWorkerTests
{
    [Test]
    public async Task ConnectionDrop_RetriesWithExponentialBackoff()
    {
        int invocationCount = 0;
        var options = new RelayMailboxStreamWorkerOptions
        {
            InitialBackoff = TimeSpan.FromMilliseconds(20),
            MaxBackoff = TimeSpan.FromMilliseconds(200),
            BackoffMultiplier = 2.0
        };

        var cts = new CancellationTokenSource();

        Task StreamRunner(CancellationToken ct)
        {
            invocationCount++;
            if (invocationCount < 4)
            {
                // Simulate connection drop / network failure
                throw new InvalidOperationException($"Connection dropped (attempt {invocationCount})");
            }

            // After 4 attempts, cancel stopping token to end worker
            cts.Cancel();
            return Task.CompletedTask;
        }

        var worker = new RelayMailboxStreamWorker(StreamRunner, options);

        // Start worker and wait for it to process retries
        await worker.StartAsync(cts.Token);
        await Task.Delay(400);
        await worker.StopAsync(CancellationToken.None);

        invocationCount.Should().BeGreaterThanOrEqualTo(4);
        worker.RetryCount.Should().BeGreaterThanOrEqualTo(3);
        worker.LastBackoffDelay.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(20));
    }
}
