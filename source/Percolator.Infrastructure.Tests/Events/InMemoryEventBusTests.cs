using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Percolator.Application.Ports;
using Percolator.Infrastructure.Events;

namespace Percolator.Infrastructure.Tests.Events;

[TestFixture]
public sealed class InMemoryEventBusTests
{
    private sealed record TestSampleEvent(Guid EventId, DateTimeOffset OccurredOnUtc, string Message) : IApplicationEvent;
    private sealed record AnotherTestEvent(Guid EventId, DateTimeOffset OccurredOnUtc, int Value) : IApplicationEvent;

    [Test]
    public void Constructor_WithNullLogger_ThrowsArgumentNullException()
    {
        var act = () => new InMemoryEventBus(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void Subscribe_WithNullHandler_ThrowsArgumentNullException()
    {
        using var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var act = () => bus.Subscribe<TestSampleEvent>(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void RegisterFaultHandler_WithNullHandler_ThrowsArgumentNullException()
    {
        using var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var act = () => bus.RegisterFaultHandler(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public async Task PublishAsync_WithNullEvent_ThrowsArgumentNullException()
    {
        using var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var act = async () => await bus.PublishAsync<TestSampleEvent>(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Test]
    public async Task PublishAsync_WithZeroSubscribers_CompletesSynchronouslyWithoutAllocation()
    {
        using var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var evt = new TestSampleEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, "ZeroSubscribers");

        var vt = bus.PublishAsync(evt);
        vt.IsCompletedSuccessfully.Should().BeTrue();
        await vt;
    }

    [Test]
    public async Task PublishAsync_DeliversToSubscribersInFifoOrder()
    {
        using var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var executionOrder = new List<int>();

        bus.Subscribe<TestSampleEvent>((_, _) =>
        {
            executionOrder.Add(1);
            return ValueTask.CompletedTask;
        });

        bus.Subscribe<TestSampleEvent>((_, _) =>
        {
            executionOrder.Add(2);
            return ValueTask.CompletedTask;
        });

        bus.Subscribe<TestSampleEvent>((_, _) =>
        {
            executionOrder.Add(3);
            return ValueTask.CompletedTask;
        });

        bus.GetSubscriberCount<TestSampleEvent>().Should().Be(3);

        var evt = new TestSampleEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, "Ordered");
        await bus.PublishAsync(evt);

        executionOrder.Should().Equal(1, 2, 3);
    }

    [Test]
    public async Task PublishAsync_PolymorphicDispatch_NotifiesBaseInterfaceSubscribers()
    {
        using var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var receivedGeneral = new List<IApplicationEvent>();
        var receivedSpecific = new List<TestSampleEvent>();

        bus.Subscribe<IApplicationEvent>((e, _) =>
        {
            receivedGeneral.Add(e);
            return ValueTask.CompletedTask;
        });

        bus.Subscribe<TestSampleEvent>((e, _) =>
        {
            receivedSpecific.Add(e);
            return ValueTask.CompletedTask;
        });

        var evt1 = new TestSampleEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, "E1");
        var evt2 = new AnotherTestEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, 42);

        await bus.PublishAsync(evt1);
        await bus.PublishAsync(evt2);

        receivedSpecific.Should().ContainSingle().Which.Should().Be(evt1);
        receivedGeneral.Should().HaveCount(2);
        receivedGeneral[0].Should().Be(evt1);
        receivedGeneral[1].Should().Be(evt2);
    }

    [Test]
    public async Task PublishAsync_WhenSubscriberThrows_FaultIsDispatchedViaChannel()
    {
        using var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var faultTcs = new TaskCompletionSource<EventDispatchError>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var token = bus.RegisterFaultHandler(err => faultTcs.TrySetResult(err));

        var executedSub2 = false;
        var thrownEx = new InvalidOperationException("Boom in subscriber 1");

        bus.Subscribe<TestSampleEvent>((_, _) => throw thrownEx);

        bus.Subscribe<TestSampleEvent>((_, _) =>
        {
            executedSub2 = true;
            return ValueTask.CompletedTask;
        });

        var evt = new TestSampleEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, "FaultTest");
        await bus.PublishAsync(evt);

        executedSub2.Should().BeTrue();

        var fault = await faultTcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fault.Event.Should().Be(evt);
        fault.Exception.Should().BeSameAs(thrownEx);
    }

    [Test]
    public async Task PublishAsync_WhenSubscriberReturnsFaultedTask_FaultIsDispatchedViaChannel()
    {
        using var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var faultTcs = new TaskCompletionSource<EventDispatchError>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var token = bus.RegisterFaultHandler(err => faultTcs.TrySetResult(err));

        var executedSub2 = false;
        var thrownEx = new InvalidOperationException("Async boom in subscriber 1");

        bus.Subscribe<TestSampleEvent>((_, _) => ValueTask.FromException(thrownEx));

        bus.Subscribe<TestSampleEvent>((_, _) =>
        {
            executedSub2 = true;
            return ValueTask.CompletedTask;
        });

        var evt = new TestSampleEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, "AsyncFaultTest");
        await bus.PublishAsync(evt);

        executedSub2.Should().BeTrue();

        var fault = await faultTcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fault.Event.Should().Be(evt);
        fault.Exception.Should().BeSameAs(thrownEx);
    }

    [Test]
    public async Task PublishAsync_WhenFaultHandlerThrows_BusContinuesCleanly()
    {
        using var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        using var token = bus.RegisterFaultHandler(_ => throw new ApplicationException("Hook failure"));

        var executedSub2 = false;
        bus.Subscribe<TestSampleEvent>((_, _) => throw new InvalidOperationException("Sub1 failure"));
        bus.Subscribe<TestSampleEvent>((_, _) =>
        {
            executedSub2 = true;
            return ValueTask.CompletedTask;
        });

        var evt = new TestSampleEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, "HookFaultTest");
        var act = async () => await bus.PublishAsync(evt);

        await act.Should().NotThrowAsync();
        executedSub2.Should().BeTrue();
    }

    [Test]
    public void DisposingFaultHandlerToken_UnregistersHandler()
    {
        using var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        bus.GetFaultHandlerCount().Should().Be(0);

        var token = bus.RegisterFaultHandler(_ => { });
        bus.GetFaultHandlerCount().Should().Be(1);

        token.Dispose();
        bus.GetFaultHandlerCount().Should().Be(0);
    }

    [Test]
    public async Task DisposeSubscription_UnsubscribesFromSubsequentEvents()
    {
        using var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var count = 0;

        var token = bus.Subscribe<TestSampleEvent>((_, _) =>
        {
            count++;
            return ValueTask.CompletedTask;
        });

        var evt = new TestSampleEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, "A");
        await bus.PublishAsync(evt);
        count.Should().Be(1);

        token.Dispose();
        token.Dispose(); // Redundant dispose is safe

        await bus.PublishAsync(evt);
        count.Should().Be(1);
    }

    [Test]
    public async Task PublishAsync_WhenUnsubscribingDuringDispatch_DoesNotThrow()
    {
        using var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        IDisposable token2 = null!;
        var executed1 = false;
        var executed2 = false;

        bus.Subscribe<TestSampleEvent>((_, _) =>
        {
            executed1 = true;
            token2.Dispose(); // Unsubscribe sub2 while dispatching
            return ValueTask.CompletedTask;
        });

        token2 = bus.Subscribe<TestSampleEvent>((_, _) =>
        {
            executed2 = true;
            return ValueTask.CompletedTask;
        });

        var evt = new TestSampleEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, "ConcurrentUnsub");
        var act = async () => await bus.PublishAsync(evt);

        await act.Should().NotThrowAsync();
        executed1.Should().BeTrue();
        executed2.Should().BeTrue();
    }

    [Test]
    public async Task PublishAsync_WithCancelledToken_ThrowsOperationCanceledException()
    {
        using var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var evt = new TestSampleEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, "Cancelled");
        var act = async () => await bus.PublishAsync(evt, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
