namespace Percolator.Infrastructure.Tests.Transport.Workers;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Percolator.Application.Delivery;
using Percolator.Application.Delivery.Events;
using Percolator.Application.Delivery.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Identities;
using Percolator.Infrastructure.Common;
using Percolator.Infrastructure.Events;
using Percolator.Infrastructure.Transport.Workers;

[TestFixture]
public sealed class OutboxDispatchHostTests
{
    private sealed class CountingOutboxRepository : IOutboxRepository
    {
        private readonly SemaphoreSlim _batchSignal = new(0);

        public int ProcessBatchCalls { get; private set; }

        public async Task WaitForBatchAsync(TimeSpan timeout)
        {
            var acquired = await _batchSignal.WaitAsync(timeout);
            acquired.Should().BeTrue("Expected OutboxWorker to process a batch within timeout");
        }

        public Task<IReadOnlyList<OutboxJob>> ClaimDuePendingJobsAsync(int batchSize, string workerInstanceId, TimeSpan leaseDuration, CancellationToken ct = default)
        {
            ProcessBatchCalls++;
            _batchSignal.Release();
            return Task.FromResult<IReadOnlyList<OutboxJob>>(Array.Empty<OutboxJob>());
        }

        public Task<IReadOnlyList<OutboxJob>> GetDuePendingJobsAsync(DateTimeOffset nowUtc, int limit, CancellationToken ct = default)
        {
            return Task.FromResult<IReadOnlyList<OutboxJob>>(Array.Empty<OutboxJob>());
        }

        public Task<OutboxJob?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<OutboxJob?>(null);
        public Task<IReadOnlyList<OutboxJob>> GetPendingJobsForOwnerAsync(PublicIdentityId ownerId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<OutboxJob>>(Array.Empty<OutboxJob>());
        public Task SaveAsync(OutboxJob job, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoOpTransportDispatcher : ITransportDispatcher
    {
        public ValueTask<DomainResult> SendAsync(OutboxJob job, CancellationToken ct = default)
            => ValueTask.FromResult(DomainResult.Success());
    }

    [Test]
    public async Task OutboxDispatchHost_WakesOnEnqueuedEvent_ProcessesBatch()
    {
        var services = new ServiceCollection();
        var repo = new CountingOutboxRepository();
        services.AddSingleton<IOutboxRepository>(repo);
        services.AddSingleton<ITransportDispatcher, NoOpTransportDispatcher>();
        services.AddSingleton<IDateTimeProvider, SystemDateTimeProvider>();
        services.AddScoped<OutboxWorker>();

        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var options = new OutboxDispatchHostOptions
        {
            BatchSize = 10,
            PollInterval = Timeout.InfiniteTimeSpan
        };

        using var host = new OutboxDispatchHost(
            scopeFactory,
            eventBus,
            options,
            NullLogger<OutboxDispatchHost>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await host.StartAsync(cts.Token);

        // Initial startup check
        await repo.WaitForBatchAsync(TimeSpan.FromSeconds(2));
        repo.ProcessBatchCalls.Should().Be(1);

        // Publish event - this must trigger event-driven wakeup without any polling timer
        await eventBus.PublishAsync(new OutboxJobEnqueuedEvent(Guid.NewGuid(), PublicIdentityId.New(), DateTimeOffset.UtcNow), cts.Token);

        // Deterministically await batch processing
        await repo.WaitForBatchAsync(TimeSpan.FromSeconds(2));
        repo.ProcessBatchCalls.Should().Be(2);

        await host.StopAsync(CancellationToken.None);
    }
}
