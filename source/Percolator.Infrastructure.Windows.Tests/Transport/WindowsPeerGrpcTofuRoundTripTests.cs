namespace Percolator.Infrastructure.Windows.Tests.Transport;

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Percolator.Application.Ingress;
using Percolator.Application.Ports;
using Percolator.Application.Session.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Common;
using Percolator.Infrastructure.Protos;
using Percolator.Infrastructure.Transport;
using Percolator.Infrastructure.Transport.Endpoints;
using Percolator.Infrastructure.Windows.Security;
using Percolator.Infrastructure.Windows.Tests.Fakes;
using Percolator.PluginSdk;

[TestFixture]
[Platform(Include = "Win", Reason = "Windows-specific round-trip test validating WindowsSChannelCertificateProvider with Kestrel and SChannel TLS 1.3.")]
public sealed class WindowsPeerGrpcTofuRoundTripTests
{
    private sealed class StubRelayProfileRepository : IRelayProfileRepository
    {
        public ValueTask<RelayHostedProfileSlot?> GetSlotAsync(BlindedProfileLocator locator, CancellationToken ct = default)
            => ValueTask.FromResult<RelayHostedProfileSlot?>(null);

        public ValueTask<DomainResult> SaveSlotAsync(RelayHostedProfileSlot slot, CancellationToken ct = default)
            => ValueTask.FromResult(DomainResult.Success());

        public ValueTask<DomainResult> DeleteSlotAsync(BlindedProfileLocator locator, ProfileWriteToken writeToken, CancellationToken ct = default)
            => ValueTask.FromResult(DomainResult.Success());
    }

    private sealed class StubRelayPreKeyRepository : IRelayPreKeyRepository
    {
        public Task<RelayHostedPreKeyBundle?> GetAsync(BlindedRoutingToken routingToken, DeviceId deviceId, CancellationToken cancellationToken = default)
            => Task.FromResult<RelayHostedPreKeyBundle?>(null);

        public Task<IReadOnlyList<RelayHostedPreKeyBundle>> GetAllForMailboxAsync(BlindedRoutingToken routingToken, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RelayHostedPreKeyBundle>>(Array.Empty<RelayHostedPreKeyBundle>());

        public Task<bool> HasActivePreKeyAsync(BlindedRoutingToken routingToken, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task SaveAsync(RelayHostedPreKeyBundle bundle, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task DeleteAsync(BlindedRoutingToken routingToken, DeviceId deviceId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class StubLocalChunkStore : ILocalChunkStore
    {
        public ValueTask<bool> HasBlobAsync(BlobId blobId, CancellationToken ct = default)
            => ValueTask.FromResult(false);

        public ValueTask<Stream> OpenReadAsync(BlobId blobId, CancellationToken ct = default)
            => throw new FileNotFoundException();

        public ValueTask SaveBlobAsync(BlobId blobId, Stream contentStream, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask DeleteBlobAsync(BlobId blobId, CancellationToken ct = default)
            => ValueTask.CompletedTask;
    }

    private sealed class StubIngressFilterService : IIngressFilterService
    {
        public ValueTask<DomainResult> CheckIngressAllowedAsync(
            Domain.Identities.PublicIdentityId recipientId,
            Domain.Identities.PublicIdentityId senderId,
            CancellationToken ct = default) =>
            ValueTask.FromResult(DomainResult.Success());
    }

    private sealed class StubEnvelopeUnwrapper : ISealedEnvelopeUnwrapper
    {
        public DomainResult<InboundEnvelope> Unwrap(MailboxEnvelope envelope, Domain.Identities.PublicIdentityId recipientId)
            => UnwrapDirect(envelope.Ciphertext, recipientId);

        public DomainResult<InboundEnvelope> UnwrapDirect(ReadOnlyMemory<byte> payloadBytes, Domain.Identities.PublicIdentityId recipientId)
            => DomainResult<InboundEnvelope>.Success(new InboundDirectEnvelope(
                Domain.Channels.ValueObjects.ChannelId.New(),
                recipientId,
                Domain.Identities.PublicIdentityId.New(),
                DeviceId.Primary,
                new Domain.Security.ValueObjects.RatchetHeader(Domain.Identities.ValueObjects.DhPublicKey.FromSpan(new byte[32]), 0, 0),
                payloadBytes.ToArray(),
                DateTimeOffset.UtcNow));
    }

    private sealed class StubIngressPipeline : Percolator.Application.Ingress.IInboundIngressPipeline
    {
        public ValueTask<DomainResult> ProcessInboundAsync(InboundEnvelope envelope, CancellationToken ct = default)
            => ValueTask.FromResult(DomainResult.Success());
    }

    private sealed class StubActiveIdentitySet : IActiveIdentitySet
    {
        public IReadOnlyCollection<Domain.Identities.PublicIdentityId> Current { get; } = new[] { Domain.Identities.PublicIdentityId.New() };
        public bool Contains(Domain.Identities.PublicIdentityId identityId) => true;
        public ValueTask<bool> AddAsync(Domain.Identities.PublicIdentityId identityId, CancellationToken ct = default) => ValueTask.FromResult(true);
        public ValueTask<bool> RemoveAsync(Domain.Identities.PublicIdentityId identityId, CancellationToken ct = default) => ValueTask.FromResult(true);
        public bool Add(Domain.Identities.PublicIdentityId identityId) => true;
        public bool Remove(Domain.Identities.PublicIdentityId identityId) => true;
    }

    [Test]
    public async Task KestrelPeerTransportServer_WithWindowsSChannelProvider_SuccessfullyCompletesTls13HandshakeAndGrpcCall()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITransportCertificateProvider, WindowsSChannelCertificateProvider>();
        services.AddSingleton<IRelayProfileRepository, StubRelayProfileRepository>();
        services.AddSingleton<IRelayPreKeyRepository, StubRelayPreKeyRepository>();
        services.AddSingleton<ILocalChunkStore, StubLocalChunkStore>();
        services.AddSingleton<IRelayMailboxCapabilityStore, FakeRelayMailboxCapabilityStore>();
        services.AddSingleton<IIngressFilterService, StubIngressFilterService>();
        services.AddSingleton<ISealedEnvelopeUnwrapper, StubEnvelopeUnwrapper>();
        services.AddSingleton<Percolator.Application.Ingress.IInboundIngressPipeline, StubIngressPipeline>();
        services.AddSingleton<IActiveIdentitySet, StubActiveIdentitySet>();
        services.AddSingleton<UnauthenticatedDeliveryEndpoint>();

        var serviceProvider = services.BuildServiceProvider();
        var certProvider = serviceProvider.GetRequiredService<ITransportCertificateProvider>();

        await using var server = new KestrelPeerTransportServer(certProvider, serviceProvider, new PeerTransportServerOptions());

        await server.StartAsync(IPAddress.Loopback, new PortNumber(0));

        server.IsRunning.Should().BeTrue();
        server.BoundPort.Should().NotBeNull();
        server.BoundPort!.Value.Value.Should().BeGreaterThan(0);

        using var channelFactory = new PeerGrpcChannelFactory(new PeerGrpcChannelFactoryOptions(), new SystemDateTimeProvider());
        var serverUri = new Uri($"https://127.0.0.1:{server.BoundPort.Value.Value}");
        var channel = channelFactory.GetOrCreateChannel(serverUri);

        var client = new UnauthenticatedDeliveryService.UnauthenticatedDeliveryServiceClient(channel);

        var request = new SealedEnvelopeProto
        {
            EncryptedPackage = ByteString.CopyFrom(new byte[] { 0x01, 0x02, 0x03 })
        };

        var response = await client.DeliverDirectAsync(request);

        response.Should().NotBeNull();
        response.Success.Should().BeTrue();

        await server.StopAsync();
        server.IsRunning.Should().BeFalse();
        server.BoundPort.Should().BeNull();
    }
}
