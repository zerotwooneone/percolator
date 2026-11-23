using Percolator.Application.Ingress;
using Percolator.Application.Ports;
using Percolator.Application.Session;
using Percolator.Application.Session.Ports;
using Percolator.Infrastructure.Tests.Maintenance;

namespace Percolator.Infrastructure.Tests.Transport.Endpoints;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Google.Protobuf;
using Grpc.Core;
using NUnit.Framework;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Protos;
using Percolator.Infrastructure.Security;
using Percolator.Infrastructure.Tests.Fakes;
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
        private readonly ConcurrentDictionary<(BlindedRoutingToken, DeviceId), RelayHostedPreKeyBundle> _bundles = new();

        public Task<RelayHostedPreKeyBundle?> GetAsync(BlindedRoutingToken routingToken, DeviceId deviceId, CancellationToken cancellationToken = default)
        {
            _bundles.TryGetValue((routingToken, deviceId), out var bundle);
            return Task.FromResult(bundle);
        }

        public Task<Percolator.Domain.Identities.ValueObjects.PreKeyBundle?> ConsumePreKeyBundleAsync(BlindedRoutingToken routingToken, DeviceId deviceId, CancellationToken cancellationToken = default)
        {
            _bundles.TryGetValue((routingToken, deviceId), out var bundle);
            if (bundle == null) return Task.FromResult<Percolator.Domain.Identities.ValueObjects.PreKeyBundle?>(null);
            var result = bundle.ConsumeBundle();
            return Task.FromResult<Percolator.Domain.Identities.ValueObjects.PreKeyBundle?>(result.Value);
        }

        public Task<IReadOnlyList<RelayHostedPreKeyBundle>> GetAllForMailboxAsync(BlindedRoutingToken routingToken, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<RelayHostedPreKeyBundle> list = _bundles.Values.Where(b => b.RoutingToken == routingToken).ToList();
            return Task.FromResult(list);
        }

        public Task<bool> HasActivePreKeyAsync(BlindedRoutingToken routingToken, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_bundles.Values.Any(b => b.RoutingToken == routingToken));
        }

        public Task SaveAsync(RelayHostedPreKeyBundle bundle, CancellationToken cancellationToken = default)
        {
            _bundles[(bundle.RoutingToken, bundle.DeviceId)] = bundle;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(BlindedRoutingToken routingToken, DeviceId deviceId, CancellationToken cancellationToken = default)
        {
            _bundles.TryRemove((routingToken, deviceId), out _);
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
    private FakeRelayMailboxCapabilityStore _capabilityStore = null!;
    private UnauthenticatedDeliveryEndpoint _endpoint = null!;
    private SignalCryptoEngine _cryptoEngine = null!;

    [SetUp]
    public void SetUp()
    {
        _profileRepo = new InMemoryProfileRepo();
        _preKeyRepo = new InMemoryPreKeyRepo();
        _chunkStore = new InMemoryChunkStore();
        _capabilityStore = new FakeRelayMailboxCapabilityStore();
        _endpoint = new UnauthenticatedDeliveryEndpoint(
            _profileRepo,
            _chunkStore,
            _capabilityStore,
            new RelayHostingPolicyHolder(RelayHostingPolicy.Default));
        _cryptoEngine = new SignalCryptoEngine(new CryptographyOptions());
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
    public async Task UploadBlindBlob_Sha256Mismatch_ThrowsInvalidArgument()
    {
        byte[] payload = new byte[1024];
        RandomNumberGenerator.Fill(payload);

        byte[] declaredFakeBlobId = new byte[32];
        RandomNumberGenerator.Fill(declaredFakeBlobId);

        var chunk = new BlindBlobChunkProto
        {
            BlobId = ByteString.CopyFrom(declaredFakeBlobId),
            ChunkIndex = 0,
            ChunkData = ByteString.CopyFrom(payload),
            IsFinalChunk = true
        };

        var reader = new FakeAsyncStreamReader<BlindBlobChunkProto>(new[] { chunk });

        Func<Task> act = async () =>
            await _endpoint.UploadBlindBlob(reader, new TestServerCallContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Contain("Uploaded blob SHA-256 digest does not match declared BlobId");
    }

    [Test]
    public async Task UploadBlindBlob_Sha256Matches_Succeeds()
    {
        byte[] payload = new byte[1024];
        RandomNumberGenerator.Fill(payload);

        byte[] correctBlobId = SHA256.HashData(payload);

        var chunk = new BlindBlobChunkProto
        {
            BlobId = ByteString.CopyFrom(correctBlobId),
            ChunkIndex = 0,
            ChunkData = ByteString.CopyFrom(payload),
            IsFinalChunk = true
        };

        var reader = new FakeAsyncStreamReader<BlindBlobChunkProto>(new[] { chunk });

        var response = await _endpoint.UploadBlindBlob(reader, new TestServerCallContext());
        response.Success.Should().BeTrue();
        response.BlobUri.Should().Contain(Convert.ToHexString(correctBlobId).ToLowerInvariant());
    }

    [Test]
    public async Task UploadBlindBlob_ConflictingBlobIdAcrossChunks_ThrowsInvalidArgument()
    {
        byte[] payload1 = new byte[512];
        byte[] payload2 = new byte[512];
        RandomNumberGenerator.Fill(payload1);
        RandomNumberGenerator.Fill(payload2);

        byte[] blobId1 = new byte[32];
        byte[] blobId2 = new byte[32];
        RandomNumberGenerator.Fill(blobId1);
        RandomNumberGenerator.Fill(blobId2);

        var chunk1 = new BlindBlobChunkProto
        {
            BlobId = ByteString.CopyFrom(blobId1),
            ChunkIndex = 0,
            ChunkData = ByteString.CopyFrom(payload1),
            IsFinalChunk = false
        };

        var chunk2 = new BlindBlobChunkProto
        {
            BlobId = ByteString.CopyFrom(blobId2),
            ChunkIndex = 1,
            ChunkData = ByteString.CopyFrom(payload2),
            IsFinalChunk = true
        };

        var reader = new FakeAsyncStreamReader<BlindBlobChunkProto>(new[] { chunk1, chunk2 });

        Func<Task> act = async () =>
            await _endpoint.UploadBlindBlob(reader, new TestServerCallContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Contain("Conflicting Blob ID declared across stream chunks");
    }

    [Test]
    public async Task UploadBlindBlob_InvalidBlobIdLength_ThrowsInvalidArgument()
    {
        byte[] payload = new byte[512];
        byte[] invalidBlobId = new byte[16]; // Invalid length (16 instead of 32)

        var chunk = new BlindBlobChunkProto
        {
            BlobId = ByteString.CopyFrom(invalidBlobId),
            ChunkIndex = 0,
            ChunkData = ByteString.CopyFrom(payload),
            IsFinalChunk = true
        };

        var reader = new FakeAsyncStreamReader<BlindBlobChunkProto>(new[] { chunk });

        Func<Task> act = async () =>
            await _endpoint.UploadBlindBlob(reader, new TestServerCallContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Contain("Blob ID must be exactly 32 bytes (SHA-256)");
    }

    private sealed class FakeAsyncStreamReader<T> : IAsyncStreamReader<T>
    {
        private readonly IEnumerator<T> _enumerator;

        public FakeAsyncStreamReader(IEnumerable<T> items)
        {
            _enumerator = items.GetEnumerator();
        }

        public T Current => _enumerator.Current;

        public Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            return Task.FromResult(_enumerator.MoveNext());
        }
    }
}
