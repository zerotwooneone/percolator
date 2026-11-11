// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/R3.spec.md before modifying this file.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Application2.Ports;
using Percolator.Desktop.Common;
using R3;

namespace Percolator.Desktop.Tests.Common;

[TestFixture]
public sealed class EventBusExtensionsTests
{
    private sealed record TestAppEvent(Guid EventId, DateTimeOffset OccurredOnUtc, string Payload) : IApplicationEvent;

    private sealed class ImmediateSynchronizationContext : SynchronizationContext
    {
        public int PostCount { get; private set; }

        public override void Post(SendOrPostCallback d, object? state)
        {
            PostCount++;
            d(state);
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            d(state);
        }
    }

    private sealed class FakeSubscriber : IEventSubscriber
    {
        public Func<TestAppEvent, CancellationToken, ValueTask>? RegisteredHandler { get; private set; }
        public bool WasDisposed { get; private set; }

        public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler) where TEvent : IApplicationEvent
        {
            RegisteredHandler = (Func<TestAppEvent, CancellationToken, ValueTask>)(object)handler;
            return new ActionDisposable(() => WasDisposed = true);
        }

        public IDisposable RegisterFaultHandler(Action<EventDispatchError> handler)
        {
            return new ActionDisposable(() => { });
        }

        private sealed class ActionDisposable : IDisposable
        {
            private readonly Action _onDispose;
            public ActionDisposable(Action onDispose) => _onDispose = onDispose;
            public void Dispose() => _onDispose();
        }
    }

    [Test]
    public void ObserveEvent_WithNullSubscriber_ThrowsArgumentNullException()
    {
        IEventSubscriber subscriber = null!;
        var act = () => subscriber.ObserveEvent<TestAppEvent>();
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public async Task ObserveEvent_EmitsEventsToR3Observable()
    {
        var fake = new FakeSubscriber();
        var received = new List<TestAppEvent>();

        using var sub = fake.ObserveEvent<TestAppEvent>().Subscribe(e => received.Add(e));

        var evt = new TestAppEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, "Hello");
        await fake.RegisteredHandler!(evt, CancellationToken.None);

        received.Should().ContainSingle().Which.Should().Be(evt);
    }

    [Test]
    public async Task ObserveEventOnUIThread_PostsToActiveSynchronizationContext()
    {
        var prevContext = SynchronizationContext.Current;
        var fakeContext = new ImmediateSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(fakeContext);

        try
        {
            var fake = new FakeSubscriber();
            var received = new List<TestAppEvent>();

            using var sub = fake.ObserveEventOnUIThread<TestAppEvent>().Subscribe(e => received.Add(e));

            var evt = new TestAppEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, "UIEvent");
            await fake.RegisteredHandler!(evt, CancellationToken.None);

            fakeContext.PostCount.Should().BeGreaterThan(0);
            received.Should().ContainSingle().Which.Should().Be(evt);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(prevContext);
        }
    }

    [Test]
    public void ObserveEvent_DisposingObservableSubscription_DisposesEventBusSubscription()
    {
        var fake = new FakeSubscriber();

        var sub = fake.ObserveEvent<TestAppEvent>().Subscribe(_ => { });
        fake.WasDisposed.Should().BeFalse();

        sub.Dispose();
        fake.WasDisposed.Should().BeTrue();
    }
}
