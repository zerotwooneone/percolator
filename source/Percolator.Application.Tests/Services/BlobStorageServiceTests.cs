using FluentAssertions;
using NUnit.Framework;
using Percolator.Application2.Ports;
using Percolator.Application2.Services;
using Percolator.Application2.Tests.TestDoubles;
using Percolator.Domain.Channels.Model;
using Percolator.Domain.Channels.Ports;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;

namespace Percolator.Application2.Tests.Services;

[TestFixture]
public sealed class BlobStorageServiceTests
{
    private FakeChannelRepository _channelRepo = null!;
    private FakeChannelRelayResolver _relayResolver = null!;
    private InMemoryStreamRegistry _streamRegistry = null!;
    private FakeBlobCryptoService _cryptoService = null!;
    private FakeRelayBlobClient _relayBlobClient = null!;
    private FakePeerStreamBlobClient _peerStreamBlobClient = null!;
    private FakeLocalChunkStore _localChunkStore = null!;
    private TestDateTimeProvider _timeProvider = null!;

    private BlobStorageService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _channelRepo = new FakeChannelRepository();
        _relayResolver = new FakeChannelRelayResolver();
        _streamRegistry = new InMemoryStreamRegistry();
        _cryptoService = new FakeBlobCryptoService();
        _relayBlobClient = new FakeRelayBlobClient();
        _peerStreamBlobClient = new FakePeerStreamBlobClient();
        _localChunkStore = new FakeLocalChunkStore();
        _timeProvider = new TestDateTimeProvider();

        _service = new BlobStorageService(
            _channelRepo,
            _relayResolver,
            _streamRegistry,
            _cryptoService,
            _relayBlobClient,
            _peerStreamBlobClient,
            _localChunkStore,
            _timeProvider);
    }

    [Test]
    public async Task UploadBlobAsync_DirectChannel_PeerOffline_ReturnsDirectPeerOfflineError()
    {
        // Arrange
        var channelId = ChannelId.New();
        var ownerId = PublicIdentityId.New();
        var peerId = PublicIdentityId.New();

        var directChannel = DirectChannel.Create(channelId, ownerId, peerId, _timeProvider).Value!;
        await _channelRepo.SaveDirectAsync(directChannel);
        // Relay is null (Direct P2P un-relayed)
        // Stream is not registered (Peer offline)

        var request = new BlobUploadRequest(
            new MemoryStream([1, 2, 3]),
            "image/jpeg",
            3,
            channelId);

        // Act
        var result = await _service.UploadBlobAsync(request);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("DIRECT_PEER_OFFLINE");
    }

    [Test]
    public async Task UploadBlobAsync_DirectChannel_PeerOnline_StagesCiphertextAndReturnsDirectP2P()
    {
        // Arrange
        var channelId = ChannelId.New();
        var ownerId = PublicIdentityId.New();
        var peerId = PublicIdentityId.New();

        var directChannel = DirectChannel.Create(channelId, ownerId, peerId, _timeProvider).Value!;
        await _channelRepo.SaveDirectAsync(directChannel);
        _streamRegistry.SetStreamActive(peerId, true);

        var request = new BlobUploadRequest(
            new MemoryStream([1, 2, 3]),
            "image/jpeg",
            3,
            channelId,
            Width: 800,
            Height: 600);

        // Act
        var result = await _service.UploadBlobAsync(request);

        // Assert
        result.IsSuccess.Should().BeTrue();
        var uploadResult = result.Value!;
        uploadResult.StorageMode.Should().Be(BlobStorageMode.DirectP2P);
        uploadResult.Reference.StorageMode.Should().Be(BlobStorageMode.DirectP2P);
        uploadResult.Reference.RelayEndpointUri.Should().BeNull();
        (await _localChunkStore.HasBlobAsync(uploadResult.Reference.Id)).Should().BeTrue();
    }

    [Test]
    public async Task UploadBlobAsync_RelayedChannel_UploadsToRelayAndStagesLocally()
    {
        // Arrange
        var channelId = ChannelId.New();
        var ownerId = PublicIdentityId.New();
        var peerId = PublicIdentityId.New();

        var directChannel = DirectChannel.Create(channelId, ownerId, peerId, _timeProvider).Value!;
        await _channelRepo.SaveDirectAsync(directChannel);

        var relayUri = new Uri("percolator://relay.example.com");
        _relayResolver.SetRelay(channelId, relayUri);

        var request = new BlobUploadRequest(
            new MemoryStream([1, 2, 3]),
            "image/png",
            3,
            channelId);

        // Act
        var result = await _service.UploadBlobAsync(request);

        // Assert
        result.IsSuccess.Should().BeTrue();
        var uploadResult = result.Value!;
        uploadResult.StorageMode.Should().Be(BlobStorageMode.ChannelRelay);
        uploadResult.Reference.RelayEndpointUri.Should().Be(relayUri.ToString());
        (await _localChunkStore.HasBlobAsync(uploadResult.Reference.Id)).Should().BeTrue();
        _relayBlobClient.UploadedBlobs.ContainsKey(uploadResult.Reference.Id).Should().BeTrue();
    }

    [Test]
    public async Task UploadBlobAsync_GroupChannel_UploadsOnceToCommonRelay()
    {
        // Arrange
        var channelId = ChannelId.New();
        var creatorId = PublicIdentityId.New();

        var groupChannel = GroupChannel.CreateGenesis(channelId, creatorId, "General", _timeProvider).Value!;
        await _channelRepo.SaveGroupAsync(groupChannel);

        var groupRelayUri = new Uri("percolator://grouprelay.example.com");
        _relayResolver.SetRelay(channelId, groupRelayUri);

        var request = new BlobUploadRequest(
            new MemoryStream([1, 2, 3, 4]),
            "video/mp4",
            4,
            channelId);

        // Act
        var result = await _service.UploadBlobAsync(request);

        // Assert
        result.IsSuccess.Should().BeTrue();
        var uploadResult = result.Value!;
        uploadResult.StorageMode.Should().Be(BlobStorageMode.ChannelRelay);
        uploadResult.Reference.RelayEndpointUri.Should().Be(groupRelayUri.ToString());
        (await _localChunkStore.HasBlobAsync(uploadResult.Reference.Id)).Should().BeTrue();
        _relayBlobClient.UploadedBlobs.ContainsKey(uploadResult.Reference.Id).Should().BeTrue();
    }

    [Test]
    public async Task DownloadBlobAsync_WhenLocallyCached_ReturnsWithoutNetworkCall()
    {
        // Arrange
        var blobId = new BlobId("test-cached-blob");
        await _localChunkStore.SaveBlobAsync(blobId, new MemoryStream([10, 20, 30]));

        var blobRef = new BlobReference
        {
            Id = blobId,
            CiphertextSha256 = new byte[32],
            EncryptionKey = new byte[32],
            BaseNonce = new byte[12],
            PlaintextSizeBytes = 3,
            CiphertextSizeBytes = 3,
            MimeType = "image/jpeg",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(7),
            StorageMode = BlobStorageMode.ChannelRelay,
            RelayEndpointUri = "percolator://relay.example.com"
        };

        var request = new BlobDownloadRequest(blobRef);

        // Act
        var result = await _service.DownloadBlobAsync(request);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value!.ContentLength.Should().Be(3);
        result.Value!.MimeType.Should().Be("image/jpeg");
        _relayBlobClient.DownloadCallCount.Should().Be(0);
    }

    private sealed class FakeChannelRepository : IChannelRepository
    {
        private readonly Dictionary<ChannelId, DirectChannel> _directs = [];
        private readonly Dictionary<ChannelId, GroupChannel> _groups = [];

        public Task<DirectChannel?> GetDirectByIdAsync(ChannelId id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_directs.GetValueOrDefault(id));

        public Task<DirectChannel?> GetDirectByPeerAsync(PublicIdentityId ownerId, PublicIdentityId remotePeerId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_directs.Values.FirstOrDefault(d => d.OwnerIdentityId == ownerId && d.RemotePeerId == remotePeerId));

        public Task<GroupChannel?> GetGroupByIdAsync(ChannelId id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_groups.GetValueOrDefault(id));

        public Task<IReadOnlyList<GroupChannel>> GetAllGroupsForOwnerAsync(PublicIdentityId ownerId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GroupChannel>>(_groups.Values.ToList());

        public Task SaveDirectAsync(DirectChannel channel, CancellationToken cancellationToken = default)
        {
            _directs[channel.Id] = channel;
            return Task.CompletedTask;
        }

        public Task SaveGroupAsync(GroupChannel channel, CancellationToken cancellationToken = default)
        {
            _groups[channel.Id] = channel;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeChannelRelayResolver : IChannelRelayResolver
    {
        private readonly Dictionary<ChannelId, Uri> _relays = [];

        public void SetRelay(ChannelId channelId, Uri relayUri) => _relays[channelId] = relayUri;

        public ValueTask<Uri?> ResolveRelayForChannelAsync(ChannelId channelId, CancellationToken ct = default) =>
            ValueTask.FromResult(_relays.GetValueOrDefault(channelId));
    }

    private sealed class FakeBlobCryptoService : IBlobCryptoService
    {
        public ValueTask<DomainResult<EncryptedBlobPackage>> EncryptAndPackageAsync(
            Stream plaintextStream,
            string mimeType,
            CancellationToken ct = default)
        {
            var memory = new MemoryStream();
            plaintextStream.CopyTo(memory);
            var bytes = memory.ToArray();

            var sha = new byte[32];
            Array.Fill(sha, (byte)0xAA);

            var key = new byte[32];
            var nonce = new byte[12];

            return ValueTask.FromResult(DomainResult<EncryptedBlobPackage>.Success(
                new EncryptedBlobPackage(
                    new MemoryStream(bytes),
                    sha,
                    key,
                    nonce,
                    bytes.Length,
                    bytes.Length)));
        }

        public ValueTask<DomainResult<Stream>> DecryptStreamAsync(
            Stream ciphertextStream,
            byte[] encryptionKey,
            byte[] baseNonce,
            long unpaddedPlaintextSize,
            CancellationToken ct = default)
        {
            var ms = new MemoryStream();
            ciphertextStream.CopyTo(ms);
            ms.Position = 0;
            return ValueTask.FromResult(DomainResult<Stream>.Success(ms));
        }
    }

    private sealed class FakeRelayBlobClient : IRelayBlobClient
    {
        public readonly Dictionary<BlobId, byte[]> UploadedBlobs = [];
        public int DownloadCallCount { get; private set; }

        public ValueTask<DomainResult<string>> UploadBlobAsync(
            Uri relayUri,
            BlobId blobId,
            Stream ciphertextStream,
            ChannelId channelId,
            IProgress<TransferProgress>? progress = null,
            CancellationToken ct = default)
        {
            using var ms = new MemoryStream();
            ciphertextStream.CopyTo(ms);
            UploadedBlobs[blobId] = ms.ToArray();
            return ValueTask.FromResult(DomainResult<string>.Success(relayUri.ToString()));
        }

        public ValueTask<DomainResult<Stream>> DownloadBlobAsync(
            Uri relayUri,
            BlobId blobId,
            IProgress<TransferProgress>? progress = null,
            CancellationToken ct = default)
        {
            DownloadCallCount++;
            if (UploadedBlobs.TryGetValue(blobId, out var bytes))
            {
                return ValueTask.FromResult(DomainResult<Stream>.Success(new MemoryStream(bytes)));
            }

            return ValueTask.FromResult(DomainResult<Stream>.Failure(new DomainError("NOT_FOUND", "Blob not found")));
        }

        public ValueTask<DomainResult> DeleteBlobAsync(Uri relayUri, BlobId blobId, CancellationToken ct = default)
        {
            UploadedBlobs.Remove(blobId);
            return ValueTask.FromResult(DomainResult.Success());
        }
    }

    private sealed class FakePeerStreamBlobClient : IPeerStreamBlobClient
    {
        public ValueTask<DomainResult<Stream>> RequestBlobFromPeerAsync(
            PublicIdentityId peerId,
            BlobId blobId,
            IProgress<TransferProgress>? progress = null,
            CancellationToken ct = default) =>
            ValueTask.FromResult(DomainResult<Stream>.Success(new MemoryStream([1, 2, 3])));
    }

    private sealed class FakeLocalChunkStore : ILocalChunkStore
    {
        private readonly Dictionary<BlobId, byte[]> _store = [];

        public ValueTask<bool> HasBlobAsync(BlobId blobId, CancellationToken ct = default) =>
            ValueTask.FromResult(_store.ContainsKey(blobId));

        public ValueTask<Stream> OpenReadAsync(BlobId blobId, CancellationToken ct = default) =>
            ValueTask.FromResult<Stream>(new MemoryStream(_store[blobId]));

        public ValueTask SaveBlobAsync(BlobId blobId, Stream contentStream, CancellationToken ct = default)
        {
            using var ms = new MemoryStream();
            contentStream.CopyTo(ms);
            _store[blobId] = ms.ToArray();
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteBlobAsync(BlobId blobId, CancellationToken ct = default)
        {
            _store.Remove(blobId);
            return ValueTask.CompletedTask;
        }
    }
}
