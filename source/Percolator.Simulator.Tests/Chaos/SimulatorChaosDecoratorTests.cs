using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Model;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Simulator.Features.Chaos;
using Percolator.Simulator.Features.Topology;

namespace Percolator.Simulator.Tests.Chaos;

[TestFixture]
public sealed class SimulatorChaosDecoratorTests
{
    private SimulatedNodeId _nodeId;
    private SimulatorChaosController _chaosController = null!;

    [SetUp]
    public void SetUp()
    {
        _nodeId = SimulatedNodeId.CreateNew();
        _chaosController = new SimulatorChaosController();
    }

    [Test]
    public void ChaosController_DefaultAction_IsPass()
    {
        var action = _chaosController.EvaluateDeliveryFault(_nodeId);
        action.Should().Be(ChaosFaultAction.Pass);
    }

    [Test]
    public void ChaosController_SetFaultAction_ReflectsNewAction()
    {
        _chaosController.SetFaultAction(_nodeId, ChaosFaultAction.DropEnvelope);
        _chaosController.EvaluateDeliveryFault(_nodeId).Should().Be(ChaosFaultAction.DropEnvelope);

        _chaosController.SetFaultAction(_nodeId, ChaosFaultAction.SimulateTransientError);
        _chaosController.EvaluateDeliveryFault(_nodeId).Should().Be(ChaosFaultAction.SimulateTransientError);
    }

    [Test]
    public void ChaosController_ResetFaults_RestoresPass()
    {
        _chaosController.SetFaultAction(_nodeId, ChaosFaultAction.DropEnvelope);
        _chaosController.ResetFaults(_nodeId);
        _chaosController.EvaluateDeliveryFault(_nodeId).Should().Be(ChaosFaultAction.Pass);
    }

    [Test]
    public async Task RelayCapabilityStoreDecorator_WhenPass_DelegatesToInner()
    {
        var inner = new InMemoryRelayMailboxStore();
        var decorator = new ChaosRelayMailboxCapabilityStoreDecorator(inner, _chaosController, _nodeId);
        _chaosController.SetFaultAction(_nodeId, ChaosFaultAction.Pass);

        var token = BlindedRoutingToken.New();
        var envelope = CreateTestRelayEnvelope("msg-1");

        await decorator.EnqueueEnvelopeAsync(token, envelope);

        inner.EnqueuedEnvelopes.Should().HaveCount(1);
        inner.EnqueuedEnvelopes[0].envelope.QueueId.Should().Be(envelope.QueueId);
    }

    [Test]
    public async Task RelayCapabilityStoreDecorator_WhenDropEnvelope_DoesNotCallInnerStore()
    {
        var inner = new InMemoryRelayMailboxStore();
        var decorator = new ChaosRelayMailboxCapabilityStoreDecorator(inner, _chaosController, _nodeId);
        _chaosController.SetFaultAction(_nodeId, ChaosFaultAction.DropEnvelope);

        var token = BlindedRoutingToken.New();
        var envelope = CreateTestRelayEnvelope("msg-2");

        await decorator.EnqueueEnvelopeAsync(token, envelope);

        inner.EnqueuedEnvelopes.Should().BeEmpty();
    }

    [Test]
    public async Task RelayCapabilityStoreDecorator_WhenSimulateTransientError_ThrowsInvalidOperationException()
    {
        var inner = new InMemoryRelayMailboxStore();
        var decorator = new ChaosRelayMailboxCapabilityStoreDecorator(inner, _chaosController, _nodeId);
        _chaosController.SetFaultAction(_nodeId, ChaosFaultAction.SimulateTransientError);

        var token = BlindedRoutingToken.New();
        var envelope = CreateTestRelayEnvelope("msg-3");

        var act = async () => await decorator.EnqueueEnvelopeAsync(token, envelope);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*transient relay failure*");
        inner.EnqueuedEnvelopes.Should().BeEmpty();
    }

    [Test]
    public async Task RelayCapabilityStoreDecorator_WhenInjectArtificialDelay_DelaysAndDelegates()
    {
        var inner = new InMemoryRelayMailboxStore();
        var decorator = new ChaosRelayMailboxCapabilityStoreDecorator(inner, _chaosController, _nodeId);
        _chaosController.SetFaultAction(_nodeId, ChaosFaultAction.InjectArtificialDelay);

        var token = BlindedRoutingToken.New();
        var envelope = CreateTestRelayEnvelope("msg-4");

        var sw = Stopwatch.StartNew();
        await decorator.EnqueueEnvelopeAsync(token, envelope);
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(400);
        inner.EnqueuedEnvelopes.Should().HaveCount(1);
    }

    private static RelayQueuedEnvelopeRecord CreateTestRelayEnvelope(string id)
    {
        var queueId = MailboxQueueId.Parse(id);
        var payload = new byte[] { 1, 2, 3, 4 };
        return new RelayQueuedEnvelopeRecord(queueId, payload, DateTimeOffset.UtcNow);
    }

    private sealed class InMemoryRelayMailboxStore : IRelayMailboxCapabilityStore
    {
        public List<(BlindedRoutingToken token, RelayQueuedEnvelopeRecord envelope)> EnqueuedEnvelopes { get; } = new();

        public ValueTask RegisterAsync(BlindedRoutingToken routingToken, DeliveryToken deliveryToken, DeliveryToken drainToken, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask<DomainResult> RegisterVerifiedAsync(BlindedRoutingToken routingToken, DeliveryToken deliveryToken, DeliveryToken drainToken, DeliveryToken? existingDrainToken = null, CancellationToken ct = default)
            => ValueTask.FromResult(DomainResult.Success());

        public ValueTask<bool> VerifyDeliveryTokenAsync(BlindedRoutingToken routingToken, DeliveryToken deliveryToken, CancellationToken ct = default)
            => ValueTask.FromResult(true);

        public ValueTask<bool> VerifyDrainTokenAsync(BlindedRoutingToken routingToken, DeliveryToken drainToken, CancellationToken ct = default)
            => ValueTask.FromResult(true);

        public ValueTask<DeliveryToken?> GetDrainTokenAsync(BlindedRoutingToken routingToken, CancellationToken ct = default)
            => ValueTask.FromResult<DeliveryToken?>(null);

        public ValueTask EnqueueEnvelopeAsync(BlindedRoutingToken routingToken, RelayQueuedEnvelopeRecord envelope, CancellationToken ct = default)
        {
            EnqueuedEnvelopes.Add((routingToken, envelope));
            return ValueTask.CompletedTask;
        }

        public ValueTask<DomainResult> EnqueueEnvelopeAsync(
            BlindedRoutingToken routingToken,
            DeliveryToken deliveryToken,
            MailboxEnvelope envelope,
            long maxMailboxBytes,
            CancellationToken ct = default)
        {
            var record = new RelayQueuedEnvelopeRecord(
                MailboxQueueId.Parse(Guid.NewGuid().ToString()),
                envelope.Ciphertext.Span.ToArray(),
                DateTimeOffset.UtcNow);
            EnqueuedEnvelopes.Add((routingToken, record));
            return ValueTask.FromResult(DomainResult.Success());
        }

        public ValueTask<DomainResult> EnqueueAuthenticatedInitialAsync(
            BlindedRoutingToken routingToken,
            MailboxEnvelope envelope,
            long maxMailboxBytes,
            CancellationToken ct = default)
        {
            var record = new RelayQueuedEnvelopeRecord(
                MailboxQueueId.Parse(Guid.NewGuid().ToString()),
                envelope.Ciphertext.Span.ToArray(),
                DateTimeOffset.UtcNow);
            EnqueuedEnvelopes.Add((routingToken, record));
            return ValueTask.FromResult(DomainResult.Success());
        }

        public ValueTask<IReadOnlyList<RelayQueuedEnvelopeRecord>> GetEnvelopesAsync(BlindedRoutingToken routingToken, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<RelayQueuedEnvelopeRecord>>(EnqueuedEnvelopes.Select(x => x.envelope).ToList());

        public ValueTask<int> PurgeMailboxAsync(BlindedRoutingToken routingToken, CancellationToken ct = default)
        {
            var count = EnqueuedEnvelopes.RemoveAll(x => x.token == routingToken);
            return ValueTask.FromResult(count);
        }

        public IDisposable SubscribeMailbox(BlindedRoutingToken routingToken, System.Threading.Channels.ChannelWriter<RelayQueuedEnvelopeRecord> writer)
            => new NoOpDisposable();

        public ValueTask<bool> AcknowledgeEnvelopeAsync(BlindedRoutingToken routingToken, MailboxQueueId queueId, CancellationToken ct = default)
            => ValueTask.FromResult(true);

        private sealed class NoOpDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }
}
