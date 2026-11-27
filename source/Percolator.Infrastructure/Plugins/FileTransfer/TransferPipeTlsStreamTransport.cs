namespace Percolator.Infrastructure.Plugins.FileTransfer;

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.MultiStreamEngine;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Apps.FileTransfer.Ports;
using Percolator.Infrastructure.Plugins.FileTransfer.WebRtc;
using Percolator.Infrastructure.Protos;
using Percolator.Infrastructure.Transport;

/// <summary>
/// Full-duplex .NET Stream over the Exchange's zero-disk bidirectional TransferPipeService.OpenPipe gRPC call.
/// Immediately sends the 32-byte random one-time PipeRendezvousToken so both TLS server and TLS client peers
/// pair cleanly without deadlocking SslStream's initial read/write ordering.
/// </summary>
internal sealed class GrpcBidirectionalPipeStream : Stream
{
    private readonly AsyncDuplexStreamingCall<PipeChunkProto, PipeChunkProto> _call;
    private readonly CancellationTokenSource _callCts;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private byte[]? _currentChunk;
    private int _currentOffset;
    private int _disposed;

    private GrpcBidirectionalPipeStream(
        AsyncDuplexStreamingCall<PipeChunkProto, PipeChunkProto> call,
        CancellationTokenSource callCts)
    {
        _call = call;
        _callCts = callCts;
    }

    public static async ValueTask<GrpcBidirectionalPipeStream> OpenAndJoinAsync(
        TransferPipeService.TransferPipeServiceClient client,
        PipeRendezvousToken rendezvousToken,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(rendezvousToken);

        var callCts = new CancellationTokenSource();
        AsyncDuplexStreamingCall<PipeChunkProto, PipeChunkProto>? call = null;
        try
        {
            call = client.OpenPipe(cancellationToken: callCts.Token);

            // Immediately transmit the 32-byte rendezvous token so the Exchange pairs both peers
            // before TLS 1.3 SslStream starts reading/writing handshake records.
            await call.RequestStream.WriteAsync(
                new PipeChunkProto
                {
                    RendezvousToken = ByteString.CopyFrom(rendezvousToken.Span)
                },
                ct).ConfigureAwait(false);

            return new GrpcBidirectionalPipeStream(call, callCts);
        }
        catch
        {
            try
            {
                callCts.Cancel();
            }
            catch
            {
            }

            call?.Dispose();
            callCts.Dispose();
            throw;
        }
    }

    public override bool CanRead => Volatile.Read(ref _disposed) == 0;
    public override bool CanSeek => false;
    public override bool CanWrite => Volatile.Read(ref _disposed) == 0;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (buffer.IsEmpty)
        {
            return 0;
        }

        while (_currentChunk == null || _currentOffset >= _currentChunk.Length)
        {
            bool moved;
            try
            {
                moved = await _call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false);
            }
            catch (RpcException ex) when (ex.StatusCode is StatusCode.Cancelled or StatusCode.Aborted)
            {
                return 0;
            }
            catch (OperationCanceledException) when (_callCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                return 0;
            }

            if (!moved)
            {
                return 0;
            }

            ByteString payload = _call.ResponseStream.Current.Payload;
            if (payload.IsEmpty)
            {
                continue;
            }

            _currentChunk = payload.ToByteArray();
            _currentOffset = 0;
        }

        int available = _currentChunk.Length - _currentOffset;
        int toCopy = Math.Min(available, buffer.Length);
        _currentChunk.AsSpan(_currentOffset, toCopy).CopyTo(buffer.Span);
        _currentOffset += toCopy;

        if (_currentOffset >= _currentChunk.Length)
        {
            _currentChunk = null;
            _currentOffset = 0;
        }

        return toCopy;
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (buffer.IsEmpty)
        {
            return;
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            int offset = 0;
            while (offset < buffer.Length)
            {
                int sliceLen = Math.Min(InMemoryTransferPipeBroker.MaxFramePayloadBytes, buffer.Length - offset);
                ReadOnlyMemory<byte> slice = buffer.Slice(offset, sliceLen);

                await _call.RequestStream.WriteAsync(
                    new PipeChunkProto
                    {
                        Payload = ByteString.CopyFrom(slice.Span)
                    },
                    cancellationToken).ConfigureAwait(false);

                offset += sliceLen;
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await _call.RequestStream.CompleteAsync().ConfigureAwait(false);
        }
        catch
        {
        }

        try
        {
            _callCts.Cancel();
        }
        catch
        {
        }

        try
        {
            _call.Dispose();
        }
        catch
        {
        }

        _callCts.Dispose();
        _writeLock.Dispose();
        await base.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// Optional fallback ITransferDataChannel wrapping the Exchange's zero-disk TransferPipe in standard
/// .NET SslStream forced to TLS 1.3 mTLS with strict E2E-pinned SHA-256 peer certificate verification
/// and mutual HandshakeFrame authentication.
/// </summary>
internal sealed class TransferPipeTlsDataChannel : ITransferDataChannel
{
    private readonly GrpcBidirectionalPipeStream _pipeStream;
    private readonly SslStream _sslStream;
    private readonly X509Certificate2 _localCertificate;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Action _onDisposed;
    private int _disposed;

    public StreamIndex Index => StreamIndex.FromInt(0);
    public string Label => "percolator-ft-pipe-tls13";
    public bool IsOpen => Volatile.Read(ref _disposed) == 0 && _sslStream.IsAuthenticated;
    public long BufferedAmount => 0;
    public TransferTransportKind TransportKind => TransferTransportKind.RelayedTransferPipeTls13;
    public CertSha256Fingerprint VerifiedRemoteCertSha256 { get; }

    private TransferPipeTlsDataChannel(
        GrpcBidirectionalPipeStream pipeStream,
        SslStream sslStream,
        X509Certificate2 localCertificate,
        CertSha256Fingerprint verifiedRemoteCertSha256,
        Action onDisposed)
    {
        _pipeStream = pipeStream;
        _sslStream = sslStream;
        _localCertificate = localCertificate;
        VerifiedRemoteCertSha256 = verifiedRemoteCertSha256;
        _onDisposed = onDisposed;
    }

    public static async ValueTask<TransferPipeTlsDataChannel> ConnectAndAuthenticateAsync(
        IPeerGrpcChannelFactory channelFactory,
        ITransferSessionCertificateStore certificateStore,
        RelayedPipeConnectionOptions options,
        Action onDisposed,
        CancellationToken ct)
    {
        TimeSpan handshakeTimeout = options.HandshakeTimeout ?? TimeSpan.FromSeconds(15);
        using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        handshakeCts.CancelAfter(handshakeTimeout);

        X509Certificate2 localCert = await LoadTransientCertificateAsync(certificateStore, options.SessionId, handshakeCts.Token)
            .ConfigureAwait(false);

        GrpcBidirectionalPipeStream? pipeStream = null;
        SslStream? sslStream = null;

        try
        {
            var grpcChannel = channelFactory.GetOrCreateChannel(options.ExchangeEndpoint.ToUri(), ChannelPoolKind.Unauthenticated);
            var client = new TransferPipeService.TransferPipeServiceClient(grpcChannel);

            pipeStream = await GrpcBidirectionalPipeStream.OpenAndJoinAsync(client, options.RendezvousToken, handshakeCts.Token)
                .ConfigureAwait(false);

            sslStream = new SslStream(pipeStream, leaveInnerStreamOpen: false);

            try
            {
                if (options.IsTlsServer)
                {
                    await sslStream.AuthenticateAsServerAsync(
                        new SslServerAuthenticationOptions
                        {
                            ServerCertificate = localCert,
                            ClientCertificateRequired = true,
                            EnabledSslProtocols = SslProtocols.Tls13,
                            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                            RemoteCertificateValidationCallback = (_, remoteCert, _, _) =>
                                ValidatePinnedCertificate(remoteCert, options.ExpectedRemoteCertSha256)
                        },
                        handshakeCts.Token).ConfigureAwait(false);
                }
                else
                {
                    await sslStream.AuthenticateAsClientAsync(
                        new SslClientAuthenticationOptions
                        {
                            TargetHost = $"percolator-ft-{options.SessionId.Value:N}",
                            ClientCertificates = new X509CertificateCollection { localCert },
                            EnabledSslProtocols = SslProtocols.Tls13,
                            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                            LocalCertificateSelectionCallback = (_, _, _, _, _) => localCert,
                            RemoteCertificateValidationCallback = (_, remoteCert, _, _) =>
                                ValidatePinnedCertificate(remoteCert, options.ExpectedRemoteCertSha256)
                        },
                        handshakeCts.Token).ConfigureAwait(false);
                }
            }
            catch (AuthenticationException ex)
            {
                throw new TransferTransportException(
                    TransferTransportErrorCodes.DtlsFingerprintMismatch,
                    $"TLS 1.3 mTLS certificate pinning failed over TransferPipe: {ex.Message}",
                    ex);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new TransferTransportException(
                    TransferTransportErrorCodes.HandshakeTimeout,
                    $"Timed out during TLS 1.3 mTLS handshake over TransferPipe ({handshakeTimeout.TotalSeconds:F1}s).",
                    ex);
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.PermissionDenied)
            {
                throw new TransferTransportException(
                    TransferTransportErrorCodes.DirectNatTraversalFailed,
                    $"Exchange rejected TransferPipe connection: {ex.Status.Detail}",
                    ex);
            }
            catch (IOException ex)
            {
                throw new TransferTransportException(
                    TransferTransportErrorCodes.DtlsFingerprintMismatch,
                    $"TLS 1.3 mTLS handshake terminated by peer over TransferPipe: {ex.Message}",
                    ex);
            }

            if (sslStream.SslProtocol != SslProtocols.Tls13 ||
                !sslStream.IsMutuallyAuthenticated ||
                !ValidatePinnedCertificate(sslStream.RemoteCertificate, options.ExpectedRemoteCertSha256))
            {
                throw new TransferTransportException(
                    TransferTransportErrorCodes.DtlsFingerprintMismatch,
                    "TransferPipe TLS session did not complete mutually authenticated TLS 1.3 with matching pinned peer certificate.");
            }

            var channel = new TransferPipeTlsDataChannel(
                pipeStream,
                sslStream,
                localCert,
                options.ExpectedRemoteCertSha256,
                onDisposed);

            // Prevent outer finally from double-disposing once owned by channel.
            pipeStream = null;
            sslStream = null;

            try
            {
                var localHandshake = new HandshakeFrame(
                    options.StreamAuthToken,
                    options.SessionId,
                    StreamIndex.FromInt(0),
                    options.LocalIdentityId);

                Task sendTask = channel.SendFrameAsync(localHandshake, handshakeCts.Token).AsTask();
                Task<IStreamFramePayload> receiveTask = channel.ReceiveFrameAsync(handshakeCts.Token).AsTask();
                try
                {
                    await Task.WhenAll(sendTask, receiveTask).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
                {
                    throw new TransferTransportException(
                        TransferTransportErrorCodes.HandshakeTimeout,
                        "Timed out waiting for peer HandshakeFrame over TransferPipe TLS 1.3.",
                        ex);
                }
                catch (Exception ex) when (ex is IOException or AuthenticationException)
                {
                    // In TLS 1.3, server-side client-certificate rejection arrives as a post-handshake fatal alert on the client's first read.
                    throw new TransferTransportException(
                        TransferTransportErrorCodes.DtlsFingerprintMismatch,
                        $"TLS 1.3 mTLS session was rejected by remote peer during authentication: {ex.Message}",
                        ex);
                }

                if (receiveTask.Result is not HandshakeFrame remoteHandshake ||
                    remoteHandshake.SessionId != options.SessionId ||
                    !CryptographicOperations.FixedTimeEquals(remoteHandshake.AuthToken.Span, options.StreamAuthToken.Span))
                {
                    throw new TransferTransportException(
                        TransferTransportErrorCodes.InvalidHandshakeToken,
                        "Peer HandshakeFrame token or session ID mismatch over TransferPipe TLS 1.3.");
                }

                return channel;
            }
            catch
            {
                await channel.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch
        {
            if (sslStream != null)
            {
                await sslStream.DisposeAsync().ConfigureAwait(false);
            }
            else if (pipeStream != null)
            {
                await pipeStream.DisposeAsync().ConfigureAwait(false);
            }

            CleanupCertificate(localCert);
            throw;
        }
    }

    public async ValueTask SendFrameAsync(IStreamFramePayload frame, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        using PooledEncodedFrame encoded = SwarmFrameCodec.EncodePooled(frame);

        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await _sslStream.WriteAsync(encoded.Memory, ct).ConfigureAwait(false);
            await _sslStream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async ValueTask<IStreamFramePayload> ReceiveFrameAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        try
        {
            return await SwarmFrameCodec.ReadFramePooledAsync(_sslStream, ct).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await _sslStream.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }

        try
        {
            await _pipeStream.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }

        CleanupCertificate(_localCertificate);
        _sendLock.Dispose();
        _onDisposed();
    }

    internal static bool ValidatePinnedCertificate(
        X509Certificate? remoteCertificate,
        CertSha256Fingerprint expectedRemoteFingerprint)
    {
        if (remoteCertificate == null)
        {
            return false;
        }

        byte[] rawDer = remoteCertificate.GetRawCertData();
        if (rawDer.Length == 0)
        {
            return false;
        }

        CertSha256Fingerprint actual = CertSha256Fingerprint.ComputeFromCertificateDer(rawDer);
        return CryptographicOperations.FixedTimeEquals(actual.Span, expectedRemoteFingerprint.Span);
    }

    private static async ValueTask<X509Certificate2> LoadTransientCertificateAsync(
        ITransferSessionCertificateStore certificateStore,
        TransferSessionId sessionId,
        CancellationToken ct)
    {
        using TransferSessionCertificateMaterial material = await certificateStore.GetOrCreateAsync(sessionId, ct)
            .ConfigureAwait(false);

        using X509Certificate2 publicCert = X509CertificateLoader.LoadCertificate(material.CertificateDer);
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(material.PrivateKeyPkcs8, out _);
        using X509Certificate2 combined = publicCert.CopyWithPrivateKey(ecdsa);

        byte[] passwordBytes = new byte[32];
        RandomNumberGenerator.Fill(passwordBytes);
        string password = Convert.ToHexString(passwordBytes);
        byte[]? pfxBytes = null;

        try
        {
            pfxBytes = combined.Export(X509ContentType.Pfx, password);
            if (OperatingSystem.IsWindows())
            {
                // SChannel requires a CNG-backed key handle for TLS 1.3 mTLS; delete the CNG key on DisposeAsync.
                return X509CertificateLoader.LoadPkcs12(
                    pfxBytes,
                    password,
                    X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
            }

            return X509CertificateLoader.LoadPkcs12(
                pfxBytes,
                password,
                X509KeyStorageFlags.EphemeralKeySet);
        }
        finally
        {
            if (pfxBytes != null)
            {
                CryptographicOperations.ZeroMemory(pfxBytes);
            }

            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    private static void CleanupCertificate(X509Certificate2 cert)
    {
        try
        {
            if (OperatingSystem.IsWindows() && cert.GetECDsaPrivateKey() is ECDsaCng cngKey)
            {
                try
                {
                    cngKey.Key.Delete();
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
        finally
        {
            cert.Dispose();
        }
    }
}

/// <summary>
/// Unified peer transport coordinator implementing ITransferPeerTransport.
/// Guarantees zero listening sockets before activation, enforces per-session peer caps,
/// manages quarantined WebRTC ICE/STUN/DTLS/SCTP direct connections and optional TLS 1.3 mTLS
/// TransferPipe fallback streams, and tears down all active handles on session stop/pause/cancel/lock.
/// </summary>
public sealed class TransferPeerTransport : ITransferPeerTransport
{
    private readonly IPeerGrpcChannelFactory _channelFactory;
    private readonly ITransferSessionCertificateStore _certificateStore;
    private readonly object _sync = new();
    private readonly Dictionary<TransferSessionId, HashSet<IAsyncDisposable>> _activeBySession = new();
    private readonly Dictionary<TransferSessionId, int> _pendingBySession = new();
    private int _activeCount;
    private int _disposed;

    public int ActiveConnectionCount => Volatile.Read(ref _activeCount);

    public TransferPeerTransport(
        IPeerGrpcChannelFactory channelFactory,
        ITransferSessionCertificateStore certificateStore)
    {
        _channelFactory = channelFactory ?? throw new ArgumentNullException(nameof(channelFactory));
        _certificateStore = certificateStore ?? throw new ArgumentNullException(nameof(certificateStore));
    }

    public ValueTask<ITransferPeerConnection> CreateDirectConnectionAsync(
        DirectPeerConnectionOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ct.ThrowIfCancellationRequested();

        ReserveSessionSlot(options.SessionId, options.MaxPeersPerSession);

        WebRtcPeerConnectionAdapter? adapter = null;
        try
        {
            adapter = new WebRtcPeerConnectionAdapter(
                options,
                () => UnregisterConnection(options.SessionId, adapter!));

            CommitReservedConnection(options.SessionId, adapter);
            return ValueTask.FromResult<ITransferPeerConnection>(adapter);
        }
        catch
        {
            ReleaseReservedSlot(options.SessionId);
            throw;
        }
    }

    public async ValueTask<ITransferDataChannel> OpenRelayedPipeChannelAsync(
        RelayedPipeConnectionOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ct.ThrowIfCancellationRequested();

        if (!options.ExchangeSupportsTransferPipe)
        {
            throw new TransferTransportException(
                TransferTransportErrorCodes.DirectNatTraversalFailed,
                "Direct NAT traversal failed and the configured Exchange does not enable TransferPipe fallback.");
        }

        ReserveSessionSlot(options.SessionId, options.MaxPeersPerSession);

        TransferPipeTlsDataChannel? channel = null;
        try
        {
            channel = await TransferPipeTlsDataChannel.ConnectAndAuthenticateAsync(
                _channelFactory,
                _certificateStore,
                options,
                () => UnregisterConnection(options.SessionId, channel!),
                ct).ConfigureAwait(false);

            CommitReservedConnection(options.SessionId, channel);
            return channel;
        }
        catch
        {
            ReleaseReservedSlot(options.SessionId);
            throw;
        }
    }

    public async ValueTask CloseSessionConnectionsAsync(
        TransferSessionId sessionId,
        CancellationToken ct = default)
    {
        List<IAsyncDisposable> toClose;
        lock (_sync)
        {
            if (!_activeBySession.TryGetValue(sessionId, out HashSet<IAsyncDisposable>? set) || set.Count == 0)
            {
                return;
            }

            toClose = new List<IAsyncDisposable>(set);
        }

        foreach (IAsyncDisposable handle in toClose)
        {
            try
            {
                await handle.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    public async ValueTask CloseAllConnectionsAsync(CancellationToken ct = default)
    {
        List<IAsyncDisposable> toClose = new();
        lock (_sync)
        {
            foreach (HashSet<IAsyncDisposable> set in _activeBySession.Values)
            {
                toClose.AddRange(set);
            }
        }

        foreach (IAsyncDisposable handle in toClose)
        {
            try
            {
                await handle.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await CloseAllConnectionsAsync().ConfigureAwait(false);
    }

    private void ReserveSessionSlot(TransferSessionId sessionId, int maxPeersPerSession)
    {
        int cap = Math.Max(1, maxPeersPerSession);
        lock (_sync)
        {
            int active = _activeBySession.TryGetValue(sessionId, out HashSet<IAsyncDisposable>? set) ? set.Count : 0;
            int pending = _pendingBySession.TryGetValue(sessionId, out int p) ? p : 0;

            if (active + pending >= cap)
            {
                throw new TransferTransportException(
                    TransferTransportErrorCodes.SessionPeerCapExceeded,
                    $"Transfer session {sessionId.Value} has reached its per-session peer cap ({cap}).");
            }

            _pendingBySession[sessionId] = pending + 1;
        }
    }

    private void CommitReservedConnection(TransferSessionId sessionId, IAsyncDisposable handle)
    {
        lock (_sync)
        {
            DecrementPendingLocked(sessionId);

            if (!_activeBySession.TryGetValue(sessionId, out HashSet<IAsyncDisposable>? set))
            {
                set = new HashSet<IAsyncDisposable>();
                _activeBySession[sessionId] = set;
            }

            if (set.Add(handle))
            {
                Interlocked.Increment(ref _activeCount);
            }
        }
    }

    private void UnregisterConnection(TransferSessionId sessionId, IAsyncDisposable? handle)
    {
        if (handle == null)
        {
            return;
        }

        lock (_sync)
        {
            if (_activeBySession.TryGetValue(sessionId, out HashSet<IAsyncDisposable>? set) && set.Remove(handle))
            {
                Interlocked.Decrement(ref _activeCount);
                if (set.Count == 0)
                {
                    _activeBySession.Remove(sessionId);
                }
            }
        }
    }

    private void ReleaseReservedSlot(TransferSessionId sessionId)
    {
        lock (_sync)
        {
            DecrementPendingLocked(sessionId);
        }
    }

    private void DecrementPendingLocked(TransferSessionId sessionId)
    {
        if (_pendingBySession.TryGetValue(sessionId, out int pending))
        {
            if (pending <= 1)
            {
                _pendingBySession.Remove(sessionId);
            }
            else
            {
                _pendingBySession[sessionId] = pending - 1;
            }
        }
    }
}
