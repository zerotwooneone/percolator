namespace Percolator.Infrastructure.Tests.Plugins.BlobStorage;

using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using FluentAssertions;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;
using Percolator.Application2.Ports;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.DependencyInjection;
using Percolator.Infrastructure.Plugins.BlobStorage;
using Percolator.Infrastructure.Protos;
using Percolator.Infrastructure.Tests.Transport.Endpoints;
using Percolator.Infrastructure.Transport.Endpoints;
using Percolator.PluginSdk;

[TestFixture]
public sealed class RelayBlobClientTests
{
    private string _tempDirectory = null!;
    private LocalFileChunkStore _chunkStore = null!;
    private UnauthenticatedDeliveryEndpoint _endpoint = null!;
    private RelayBlobClient _directClient = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "RelayBlobClientTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _chunkStore = new LocalFileChunkStore(_tempDirectory, maxBlobSizeBytes: 10 * 1024 * 1024, maxCacheQuotaBytes: 50 * 1024 * 1024);

        _endpoint = new UnauthenticatedDeliveryEndpoint(
            new UnauthenticatedDeliveryEndpointTestsFakes.StubProfileRepo(),
            new UnauthenticatedDeliveryEndpointTestsFakes.StubPreKeyRepo(),
            _chunkStore);

        _directClient = new RelayBlobClient(uri => new DirectEndpointClient(_endpoint));
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDirectory))
        {
            try { Directory.Delete(_tempDirectory, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task UploadBlobAsync_StreamExceeds100Mb_ReturnsFailureWithoutNetworkCall()
    {
        var client = new RelayBlobClient();
        var oversizedStream = new FakeOversizedStream(101L * 1024 * 1024);
        var blobId = BlobId.FromSha256(new byte[32]);
        var channelId = ChannelId.New();

        var result = await client.UploadBlobAsync(
            new Uri("http://localhost:5000"),
            blobId,
            oversizedStream,
            channelId);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().NotBeNull();
        result.Error!.Code.Should().Be("BLOB_TOO_LARGE");
    }

    [Test]
    public async Task DownloadBlobAsync_NotFound_ReturnsDomainFailure()
    {
        var client = new RelayBlobClient(uri => new FakeNotFoundClient());
        var blobId = BlobId.FromSha256(new byte[32]);

        var result = await client.DownloadBlobAsync(
            new Uri("http://localhost:5000"),
            blobId);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().NotBeNull();
        result.Error!.Code.Should().Be("NOT_FOUND");
    }

    [Test]
    public async Task RoundTrip_UploadDownloadAndDelete_TransfersRealChunksSuccessfully()
    {
        // 150 KB payload requires 3 chunks (64 KB + 64 KB + 22 KB)
        byte[] payload = new byte[150 * 1024];
        RandomNumberGenerator.Fill(payload);

        byte[] hash = SHA256.HashData(payload);
        var blobId = BlobId.FromSha256(hash);
        var channelId = ChannelId.New();
        var relayUri = new Uri("http://localhost:5000");

        var uploadProgressList = new System.Collections.Generic.List<TransferProgress>();
        var uploadProgress = new Progress<TransferProgress>(p => uploadProgressList.Add(p));

        using var uploadStream = new MemoryStream(payload);
        var uploadResult = await _directClient.UploadBlobAsync(
            relayUri,
            blobId,
            uploadStream,
            channelId,
            uploadProgress);

        uploadResult.IsSuccess.Should().BeTrue();
        uploadResult.Value.Should().Contain(blobId.HexDigest.ToLowerInvariant());

        // Verify blob exists in underlying store
        bool existsInStore = await _chunkStore.HasBlobAsync(blobId);
        existsInStore.Should().BeTrue();

        // Download blob
        var downloadResult = await _directClient.DownloadBlobAsync(relayUri, blobId);
        downloadResult.IsSuccess.Should().BeTrue();
        downloadResult.Value.Should().NotBeNull();

        using var downloadedStream = downloadResult.Value!;
        using var readMs = new MemoryStream();
        await downloadedStream.CopyToAsync(readMs);

        byte[] downloadedBytes = readMs.ToArray();
        downloadedBytes.Should().Equal(payload);

        // Delete blob
        var deleteResult = await _directClient.DeleteBlobAsync(relayUri, blobId);
        deleteResult.IsSuccess.Should().BeTrue();

        // Verify blob no longer in store
        (await _chunkStore.HasBlobAsync(blobId)).Should().BeFalse();
    }

    [Test]
    public void AddBlobStorageInfrastructure_RegistersAllPortsAndHostedServices()
    {
        var services = new ServiceCollection();
        services.AddBlobStorageInfrastructure();
        var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });

        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IBlobCryptoService>().Should().BeOfType<StreamingAesGcmCryptoService>();
        scope.ServiceProvider.GetRequiredService<ILocalChunkStore>().Should().BeOfType<LocalFileChunkStore>();
        scope.ServiceProvider.GetRequiredService<IRelayBlobClient>().Should().BeOfType<RelayBlobClient>();
        scope.ServiceProvider.GetRequiredService<IPeerStreamBlobClient>().Should().BeOfType<PeerStreamBlobClient>();
        scope.ServiceProvider.GetRequiredService<IChannelRelayResolver>().Should().BeOfType<ChannelRelayResolver>();

        var hostedServices = provider.GetServices<IHostedService>();
        hostedServices.Should().ContainSingle(s => s is RelayBlobRetentionPruner);
    }

    private sealed class FakeOversizedStream : MemoryStream
    {
        private readonly long _length;

        public FakeOversizedStream(long length)
        {
            _length = length;
        }

        public override long Length => _length;
        public override bool CanSeek => true;
    }

    private sealed class FakeNotFoundClient : UnauthenticatedDeliveryService.UnauthenticatedDeliveryServiceClient
    {
        public override AsyncServerStreamingCall<BlindBlobChunkProto> DownloadBlindBlob(
            BlindBlobDownloadRequestProto request,
            CallOptions options)
        {
            var status = new Status(StatusCode.NotFound, "Blob not found");
            throw new RpcException(status);
        }
    }

    private sealed class DirectEndpointClient : UnauthenticatedDeliveryService.UnauthenticatedDeliveryServiceClient
    {
        private readonly UnauthenticatedDeliveryEndpoint _endpoint;

        public DirectEndpointClient(UnauthenticatedDeliveryEndpoint endpoint)
        {
            _endpoint = endpoint;
        }

        public override AsyncClientStreamingCall<BlindBlobChunkProto, BlindBlobUploadResponseProto> UploadBlindBlob(
            CallOptions options)
        {
            var channel = Channel.CreateUnbounded<BlindBlobChunkProto>();
            var responseTcs = new TaskCompletionSource<BlindBlobUploadResponseProto>();

            _ = Task.Run(async () =>
            {
                try
                {
                    var reader = new ChannelAsyncStreamReader<BlindBlobChunkProto>(channel.Reader);
                    var response = await _endpoint.UploadBlindBlob(reader, new TestServerCallContext(options.CancellationToken));
                    responseTcs.SetResult(response);
                }
                catch (Exception ex)
                {
                    responseTcs.SetException(ex);
                }
            });

            var writer = new ChannelClientStreamWriter<BlindBlobChunkProto>(channel.Writer);

            return new AsyncClientStreamingCall<BlindBlobChunkProto, BlindBlobUploadResponseProto>(
                writer,
                responseTcs.Task,
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { });
        }

        public override AsyncServerStreamingCall<BlindBlobChunkProto> DownloadBlindBlob(
            BlindBlobDownloadRequestProto request,
            CallOptions options)
        {
            var channel = Channel.CreateUnbounded<BlindBlobChunkProto>();

            _ = Task.Run(async () =>
            {
                try
                {
                    var writer = new ChannelServerStreamWriter<BlindBlobChunkProto>(channel.Writer);
                    await _endpoint.DownloadBlindBlob(request, writer, new TestServerCallContext(options.CancellationToken));
                    channel.Writer.Complete();
                }
                catch (Exception ex)
                {
                    channel.Writer.Complete(ex);
                }
            });

            var reader = new ChannelAsyncStreamReader<BlindBlobChunkProto>(channel.Reader);

            return new AsyncServerStreamingCall<BlindBlobChunkProto>(
                reader,
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { });
        }

        public override AsyncUnaryCall<BlindBlobDeleteResponseProto> DeleteBlindBlobAsync(
            BlindBlobDeleteRequestProto request,
            CallOptions options)
        {
            var task = _endpoint.DeleteBlindBlob(request, new TestServerCallContext(options.CancellationToken));
            return new AsyncUnaryCall<BlindBlobDeleteResponseProto>(
                task,
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { });
        }
    }

    private sealed class ChannelClientStreamWriter<T> : IClientStreamWriter<T>
    {
        private readonly ChannelWriter<T> _writer;

        public ChannelClientStreamWriter(ChannelWriter<T> writer)
        {
            _writer = writer;
        }

        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(T message) => _writer.WriteAsync(message).AsTask();

        public Task CompleteAsync()
        {
            _writer.Complete();
            return Task.CompletedTask;
        }
    }

    private sealed class ChannelServerStreamWriter<T> : IServerStreamWriter<T>
    {
        private readonly ChannelWriter<T> _writer;

        public ChannelServerStreamWriter(ChannelWriter<T> writer)
        {
            _writer = writer;
        }

        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(T message) => _writer.WriteAsync(message).AsTask();

        public Task WriteAsync(T message, CancellationToken cancellationToken) => _writer.WriteAsync(message, cancellationToken).AsTask();
    }

    private sealed class ChannelAsyncStreamReader<T> : IAsyncStreamReader<T>
    {
        private readonly ChannelReader<T> _reader;
        private T? _current;

        public ChannelAsyncStreamReader(ChannelReader<T> reader)
        {
            _reader = reader;
        }

        public T Current => _current ?? throw new InvalidOperationException("No current element.");

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            while (await _reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_reader.TryRead(out _current))
                {
                    return true;
                }
            }
            return false;
        }
    }
}

internal static class UnauthenticatedDeliveryEndpointTestsFakes
{
    public sealed class StubProfileRepo : IRelayProfileRepository
    {
        public ValueTask<RelayHostedProfileSlot?> GetSlotAsync(BlindedProfileLocator locator, CancellationToken ct = default) => ValueTask.FromResult<RelayHostedProfileSlot?>(null);
        public ValueTask<DomainResult> SaveSlotAsync(RelayHostedProfileSlot slot, CancellationToken ct = default) => ValueTask.FromResult(DomainResult.Success());
        public ValueTask<DomainResult> DeleteSlotAsync(BlindedProfileLocator locator, ProfileWriteToken writeToken, CancellationToken ct = default) => ValueTask.FromResult(DomainResult.Success());
    }

    public sealed class StubPreKeyRepo : IRelayPreKeyRepository
    {
        public Task<RelayHostedPreKeyBundle?> GetAsync(PublicIdentityId ownerId, DeviceId deviceId, CancellationToken cancellationToken = default) => Task.FromResult<RelayHostedPreKeyBundle?>(null);
        public Task<System.Collections.Generic.IReadOnlyList<RelayHostedPreKeyBundle>> GetAllForIdentityAsync(PublicIdentityId ownerId, CancellationToken cancellationToken = default) => Task.FromResult<System.Collections.Generic.IReadOnlyList<RelayHostedPreKeyBundle>>(Array.Empty<RelayHostedPreKeyBundle>());
        public Task<bool> HasActivePreKeyAsync(PublicIdentityId ownerId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task SaveAsync(RelayHostedPreKeyBundle bundle, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(PublicIdentityId ownerId, DeviceId deviceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
