namespace Percolator.Infrastructure.Tests.Transport.Endpoints;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Google.Protobuf;
using Grpc.Core;
using NUnit.Framework;
using Percolator.Application2.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Protos;
using Percolator.Infrastructure.Security;
using Percolator.Infrastructure.Transport;
using Percolator.Infrastructure.Transport.Endpoints;
using Percolator.PluginSdk;
using Signal.Interop;

[TestFixture]
public sealed class UnauthenticatedDeliveryEndpointTests
{
    private sealed class InMemoryProfileRepo : IRelayProfileRepository
    {
        private readonly ConcurrentDictionary<BlindedProfileLocator, RelayHostedProfileSlot> _slots = new();

        public ValueTask<RelayHostedProfileSlot?> GetSlotAsync(BlindedProfileLocator locator, CancellationToken ct = default)
        {
            _slots.TryGetValue(locator, out var slot);
            return ValueTask.FromResult(slot);
        }

        public ValueTask<DomainResult> SaveSlotAsync(RelayHostedProfileSlot slot, CancellationToken ct = default)
        {
            _slots[slot.Id] = slot;
            return ValueTask.FromResult(DomainResult.Success());
        }

        public ValueTask<DomainResult> DeleteSlotAsync(BlindedProfileLocator locator, ProfileWriteToken writeToken, CancellationToken ct = default)
        {
            _slots.TryRemove(locator, out _);
            return ValueTask.FromResult(DomainResult.Success());
        }
    }

    private sealed class InMemoryPreKeyRepo : IRelayPreKeyRepository
    {
        private readonly ConcurrentDictionary<(PublicIdentityId, DeviceId), RelayHostedPreKeyBundle> _bundles = new();

        public Task<RelayHostedPreKeyBundle?> GetAsync(PublicIdentityId ownerId, DeviceId deviceId, CancellationToken cancellationToken = default)
        {
            _bundles.TryGetValue((ownerId, deviceId), out var bundle);
            return Task.FromResult(bundle);
        }

        public Task<IReadOnlyList<RelayHostedPreKeyBundle>> GetAllForIdentityAsync(PublicIdentityId ownerId, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<RelayHostedPreKeyBundle> list = new List<RelayHostedPreKeyBundle>(_bundles.Values);
            return Task.FromResult(list);
        }

        public Task<bool> HasActivePreKeyAsync(PublicIdentityId ownerId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_bundles.Count > 0);
        }

        public Task SaveAsync(RelayHostedPreKeyBundle bundle, CancellationToken cancellationToken = default)
        {
            _bundles[(bundle.OwnerId, bundle.DeviceId)] = bundle;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(PublicIdentityId ownerId, DeviceId deviceId, CancellationToken cancellationToken = default)
        {
            _bundles.TryRemove((ownerId, deviceId), out _);
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryChunkStore : ILocalChunkStore
    {
        private readonly ConcurrentDictionary<BlobId, byte[]> _store = new();

        public void AddBlob(BlobId id, byte[] data) => _store[id] = data;

        public ValueTask<bool> HasBlobAsync(BlobId blobId, CancellationToken ct = default)
            => ValueTask.FromResult(_store.ContainsKey(blobId));

        public ValueTask<Stream> OpenReadAsync(BlobId blobId, CancellationToken ct = default)
        {
            if (_store.TryGetValue(blobId, out var data))
            {
                return ValueTask.FromResult<Stream>(new MemoryStream(data));
            }
            throw new FileNotFoundException();
        }

        public ValueTask SaveBlobAsync(BlobId blobId, Stream contentStream, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask DeleteBlobAsync(BlobId blobId, CancellationToken ct = default) => ValueTask.CompletedTask;
    }

    private InMemoryProfileRepo _profileRepo = null!;
    private InMemoryPreKeyRepo _preKeyRepo = null!;
    private InMemoryChunkStore _chunkStore = null!;
    private InMemoryRelayMailboxCapabilityStore _capabilityStore = null!;
    private UnauthenticatedDeliveryEndpoint _endpoint = null!;
    private SignalCryptoEngine _cryptoEngine = null!;

    [SetUp]
    public void SetUp()
    {
        _profileRepo = new InMemoryProfileRepo();
        _preKeyRepo = new InMemoryPreKeyRepo();
        _chunkStore = new InMemoryChunkStore();
        _capabilityStore = new InMemoryRelayMailboxCapabilityStore();
        _endpoint = new UnauthenticatedDeliveryEndpoint(_profileRepo, _preKeyRepo, _chunkStore, _capabilityStore);
        _cryptoEngine = new SignalCryptoEngine();
    }

    [Test]
    public async Task DeliverDirect_ValidEnvelope_ReturnsSuccessAck()
    {
        var request = new SealedEnvelopeProto
        {
            EncryptedPackage = ByteString.CopyFrom(new byte[] { 1, 2, 3, 4 })
        };

        var ack = await _endpoint.DeliverDirect(request, new TestServerCallContext());
        ack.Success.Should().BeTrue();
    }

    [Test]
    public async Task UploadProfile_PersistsSlot()
    {
        byte[] locatorBytes = new byte[32];
        byte[] writeTokenBytes = new byte[32];
        byte[] nonceBytes = new byte[12];
        byte[] ciphertextBytes = new byte[RelayHostedProfileSlot.ExactPackageSizeBytes];

        RandomNumberGenerator.Fill(locatorBytes);
        RandomNumberGenerator.Fill(writeTokenBytes);
        RandomNumberGenerator.Fill(nonceBytes);
        RandomNumberGenerator.Fill(ciphertextBytes);

        var request = new ProfileUploadRequestProto
        {
            BlindedLocator = ByteString.CopyFrom(locatorBytes),
            WriteToken = ByteString.CopyFrom(writeTokenBytes),
            Revision = 1,
            Nonce = ByteString.CopyFrom(nonceBytes),
            Ciphertext = ByteString.CopyFrom(ciphertextBytes)
        };

        var response = await _endpoint.UploadProfile(request, new TestServerCallContext());
        response.Success.Should().BeTrue();

        var slot = await _profileRepo.GetSlotAsync(BlindedProfileLocator.FromSpan(locatorBytes));
        slot.Should().NotBeNull();
        slot!.Revision.Should().Be(1);
    }

    [Test]
    public async Task DownloadBlindBlob_MalformedId_ThrowsInvalidArgument()
    {
        var malformedRequest = new BlindBlobDownloadRequestProto
        {
            BlobId = ByteString.CopyFrom(new byte[] { 1, 2, 3 }) // 3 bytes instead of 32
        };

        var writer = new TestServerStreamWriter<BlindBlobChunkProto>();

        Func<Task> act = async () =>
            await _endpoint.DownloadBlindBlob(malformedRequest, writer, new TestServerCallContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Test]
    public async Task FetchPreKeyBundle_InvalidDeliveryToken_RejectsWithoutConsumingOpk()
    {
        var ownerId = PublicIdentityId.New();
        var deviceId = new DeviceId(1);
        var routingToken = new byte[16];
        var correctDeliveryToken = new byte[32];
        var wrongDeliveryToken = new byte[32];
        var drainToken = new byte[32];

        RandomNumberGenerator.Fill(routingToken);
        RandomNumberGenerator.Fill(correctDeliveryToken);
        RandomNumberGenerator.Fill(wrongDeliveryToken);
        RandomNumberGenerator.Fill(drainToken);

        // Register mailbox with owner association
        await _capabilityStore.RegisterAsync(routingToken, correctDeliveryToken, drainToken, ownerId, deviceId);

        byte[] privId = new byte[32];
        byte[] pubIdBytes = new byte[32];
        SignalCrypto.GenerateEd25519KeyPair(privId, pubIdBytes);
        var pubId = IdentityKey.FromSpan(pubIdBytes);

        var (_, pubSigned) = _cryptoEngine.GenerateEphemeralKeyPair();
        var sig = _cryptoEngine.SignEd25519(privId, pubSigned.Span);

        var (_, opkPub) = _cryptoEngine.GenerateEphemeralKeyPair();
        var opks = new List<(uint KeyId, DhPublicKey Key)> { (101u, opkPub) };

        var bundle = RelayHostedPreKeyBundle.Create(
            ownerId,
            deviceId,
            pubId,
            pubSigned,
            DeviceLinkProof.FromSpan(sig),
            opks,
            RelayHostingPolicy.Default,
            _cryptoEngine).Value!;

        await _preKeyRepo.SaveAsync(bundle);
        bundle.AvailableOneTimePreKeyCount.Should().Be(1);

        // Fetch with wrong delivery token capability
        var invalidRequest = new FetchPreKeyBundleRequest
        {
            RecipientRoutingToken = ByteString.CopyFrom(routingToken),
            DeliveryToken = ByteString.CopyFrom(wrongDeliveryToken)
        };

        Func<Task> act = async () =>
            await _endpoint.FetchPreKeyBundle(invalidRequest, new TestServerCallContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);

        // Verify OPK was NOT consumed
        var storedBundle = await _preKeyRepo.GetAsync(ownerId, deviceId);
        storedBundle!.AvailableOneTimePreKeyCount.Should().Be(1);

        // Now fetch with CORRECT delivery token capability
        var validRequest = new FetchPreKeyBundleRequest
        {
            RecipientRoutingToken = ByteString.CopyFrom(routingToken),
            DeliveryToken = ByteString.CopyFrom(correctDeliveryToken)
        };

        var response = await _endpoint.FetchPreKeyBundle(validRequest, new TestServerCallContext());
        response.OneTimePrekey.Should().NotBeNull();
        response.OneTimePrekeyId.Should().Be(101u);

        // Verify OPK WAS consumed
        var consumedBundle = await _preKeyRepo.GetAsync(ownerId, deviceId);
        consumedBundle!.AvailableOneTimePreKeyCount.Should().Be(0);
    }
}
