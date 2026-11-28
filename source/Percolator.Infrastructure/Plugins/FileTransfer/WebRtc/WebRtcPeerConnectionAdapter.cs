namespace Percolator.Infrastructure.Plugins.FileTransfer.WebRtc;

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using DataChannelDotnet;
using DataChannelDotnet.Bindings;
using DataChannelDotnet.Data;
using DataChannelDotnet.Impl;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.MultiStreamEngine;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Apps.FileTransfer.Ports;
using Percolator.Domain.Common;
using Percolator.PluginSdk.Events;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

/// <summary>
/// Quarantined adapter wrapping DataChannelDotnet RtcPeerConnection behind ITransferPeerConnection.
/// Enforces Exchange-only STUN configuration, ECDSA P-256 DTLS certificates, data-channel-only
/// SDP validation, constant-time DTLS SHA-256 fingerprint pinning, mutual HandshakeFrame
/// verification, and off-thread native teardown.
/// </summary>
internal sealed class WebRtcPeerConnectionAdapter : ITransferPeerConnection
{
    public const string PrimaryDataChannelLabel = "percolator-ft-0";
    private static readonly TimeSpan DefaultHandshakeTimeout = TimeSpan.FromSeconds(15);
    private const int MaxCandidateLengthChars = 2048;

    private static int _nativePreloaded;

    private readonly DirectPeerConnectionOptions _options;
    private readonly IEventPublisher _eventPublisher;
    private readonly IDateTimeProvider _clock;
    private readonly Action _onDisposed;
    private readonly RtcPeerConnection _peerConnection;
    private readonly TimeSpan _handshakeTimeout;

    private readonly TaskCompletionSource<string> _localDescriptionTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<WebRtcDataChannelAdapter> _channelAvailableTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<TransferIceCandidateDiscoveredEvent> _candidateQueue;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly Task _candidatePumpTask;

    // Strong delegate references retained for the full lifetime of the native handle.
    private readonly Action<IRtcPeerConnection, RtcDescription> _onLocalDescriptionHandler;
    private readonly Action<IRtcPeerConnection, RtcCandidate> _onCandidateHandler;
    private readonly Action<IRtcPeerConnection, IRtcDataChannel> _onDataChannelHandler;
    private readonly Action<IRtcPeerConnection, rtcState> _onConnectionStateChangeHandler;

    private readonly object _channelSync = new();
    private WebRtcDataChannelAdapter? _dataChannel;
    private int _disposed;

    public TransferSessionId SessionId => _options.SessionId;
    public PublicIdentityId RemotePeerId => _options.RemotePeerId;
    public TransferTransportKind TransportKind => TransferTransportKind.DirectWebRtcDtls;
    public CertSha256Fingerprint? LocalDtlsFingerprint { get; private set; }
    public CertSha256Fingerprint? RemoteDtlsFingerprint { get; private set; }

    public WebRtcPeerConnectionAdapter(
        DirectPeerConnectionOptions options,
        IEventPublisher eventPublisher,
        IDateTimeProvider clock,
        Action onDisposed)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _eventPublisher = eventPublisher ?? throw new ArgumentNullException(nameof(eventPublisher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _onDisposed = onDisposed ?? throw new ArgumentNullException(nameof(onDisposed));
        _handshakeTimeout = options.HandshakeTimeout ?? DefaultHandshakeTimeout;

        EnsureNativeRuntimePreloaded();

        var iceServers = new List<string>();
        if (options.ExchangeStunEndpoint != null)
        {
            string host = options.ExchangeStunEndpoint.Host.Value;
            string formattedHost = host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('[')
                ? $"[{host}]"
                : host;
            iceServers.Add($"stun:{formattedHost}:{options.ExchangeStunEndpoint.Port.Value}");
        }

        var config = new RtcPeerConfiguration
        {
            IceServers = iceServers,
            CertificateType = rtcCertificateType.RTC_CERTIFICATE_ECDSA,
            DisableAutoNegotiation = true,
            MaxMessageSize = SwarmFrameCodec.MaxWireFrameSizeBytes + 64
        };

        _candidateQueue = Channel.CreateUnbounded<TransferIceCandidateDiscoveredEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        _candidatePumpTask = Task.Run(PumpOutboundCandidatesAsync);

        _peerConnection = new RtcPeerConnection(config);

        _onLocalDescriptionHandler = OnLocalDescription;
        _onCandidateHandler = OnLocalCandidate;
        _onDataChannelHandler = OnRemoteDataChannel;
        _onConnectionStateChangeHandler = OnConnectionStateChange;

        _peerConnection.OnLocalDescriptionSafe += _onLocalDescriptionHandler;
        _peerConnection.OnCandidateSafe += _onCandidateHandler;
        _peerConnection.OnDataChannel += _onDataChannelHandler;
        _peerConnection.OnConnectionStateChange += _onConnectionStateChangeHandler;
    }

    public async ValueTask<string> CreateOfferAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();

        IRtcDataChannel nativeChannel = _peerConnection.CreateDataChannel(new RtcCreateDataChannelArgs
        {
            Label = PrimaryDataChannelLabel,
            Unordered = false,
            Unreliable = false
        });

        AttachDataChannel(nativeChannel);

        _peerConnection.SetLocalDescription(RtcDescriptionType.Offer);

        return await _localDescriptionTcs.Task.WaitAsync(_handshakeTimeout, ct).ConfigureAwait(false);
    }

    public async ValueTask<string> AcceptOfferAndCreateAnswerAsync(
        string remoteSdpOffer,
        CertSha256Fingerprint? expectedRemoteDtlsFingerprint,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();

        // Validate media restrictions and constant-time DTLS fingerprint BEFORE native SDP ingestion.
        CertSha256Fingerprint verifiedRemoteFingerprint = SdpSecurityValidator.ValidateOrThrow(
            remoteSdpOffer,
            expectedRemoteDtlsFingerprint);

        RemoteDtlsFingerprint = verifiedRemoteFingerprint;

        _peerConnection.SetRemoteDescription(new RtcDescription
        {
            Sdp = remoteSdpOffer,
            Type = RtcDescriptionType.Offer
        });

        _peerConnection.SetLocalDescription(RtcDescriptionType.Answer);

        return await _localDescriptionTcs.Task.WaitAsync(_handshakeTimeout, ct).ConfigureAwait(false);
    }

    public ValueTask ApplyRemoteAnswerAsync(
        string remoteSdpAnswer,
        CertSha256Fingerprint? expectedRemoteDtlsFingerprint,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        // Validate media restrictions and constant-time DTLS fingerprint BEFORE native SDP ingestion.
        CertSha256Fingerprint verifiedRemoteFingerprint = SdpSecurityValidator.ValidateOrThrow(
            remoteSdpAnswer,
            expectedRemoteDtlsFingerprint);

        RemoteDtlsFingerprint = verifiedRemoteFingerprint;

        _peerConnection.SetRemoteDescription(new RtcDescription
        {
            Sdp = remoteSdpAnswer,
            Type = RtcDescriptionType.Answer
        });

        return ValueTask.CompletedTask;
    }

    public ValueTask AddRemoteIceCandidateAsync(
        string candidate,
        string mid,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > MaxCandidateLengthChars)
        {
            throw new ArgumentException("ICE candidate is empty or exceeds maximum length.", nameof(candidate));
        }

        _peerConnection.AddRemoteCandidate(new RtcCandidate
        {
            Content = candidate,
            Mid = mid ?? string.Empty
        });

        return ValueTask.CompletedTask;
    }

    public async ValueTask<ITransferDataChannel> WaitForAuthenticatedChannelAsync(
        StreamAuthToken expectedAuthToken,
        PublicIdentityId localIdentityId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expectedAuthToken);
        ThrowIfDisposed();

        using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        handshakeCts.CancelAfter(_handshakeTimeout);

        WebRtcDataChannelAdapter channel;
        try
        {
            channel = await _channelAvailableTcs.Task.WaitAsync(handshakeCts.Token).ConfigureAwait(false);
            await channel.OpenedTask.WaitAsync(handshakeCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            await DisposeAsync().ConfigureAwait(false);
            throw new TransferTransportException(
                TransferTransportErrorCodes.HandshakeTimeout,
                $"WebRTC ICE/DTLS DataChannel did not open within {_handshakeTimeout.TotalSeconds:F1}s.",
                ex);
        }
        catch (TransferTransportException)
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            await DisposeAsync().ConfigureAwait(false);
            throw new TransferTransportException(
                TransferTransportErrorCodes.DirectNatTraversalFailed,
                $"WebRTC DataChannel failed to open: {ex.Message}",
                ex);
        }

        CertSha256Fingerprint? remoteFingerprint = RemoteDtlsFingerprint;
        if (remoteFingerprint == null)
        {
            await DisposeAsync().ConfigureAwait(false);
            throw new TransferTransportException(
                TransferTransportErrorCodes.DtlsFingerprintMismatch,
                "Remote DTLS certificate fingerprint was not verified prior to channel authentication.");
        }

        var localHandshake = new HandshakeFrame(
            expectedAuthToken,
            SessionId,
            StreamIndex.FromInt(0),
            localIdentityId);

        IStreamFramePayload peerFrame;
        try
        {
            Task sendTask = channel.SendFrameAsync(localHandshake, handshakeCts.Token).AsTask();
            Task<IStreamFramePayload> receiveTask = channel.ReceiveFrameAsync(handshakeCts.Token).AsTask();
            await Task.WhenAll(sendTask, receiveTask).ConfigureAwait(false);
            peerFrame = receiveTask.Result;
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            await DisposeAsync().ConfigureAwait(false);
            throw new TransferTransportException(
                TransferTransportErrorCodes.HandshakeTimeout,
                "Timed out waiting for peer HandshakeFrame over WebRTC DataChannel.",
                ex);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            await DisposeAsync().ConfigureAwait(false);
            throw new TransferTransportException(
                TransferTransportErrorCodes.InvalidHandshakeToken,
                $"Peer HandshakeFrame exchange failed: {ex.Message}",
                ex);
        }

        if (peerFrame is not HandshakeFrame remoteHandshake ||
            remoteHandshake.SessionId != SessionId ||
            !CryptographicOperations.FixedTimeEquals(remoteHandshake.AuthToken.Span, expectedAuthToken.Span))
        {
            await DisposeAsync().ConfigureAwait(false);
            throw new TransferTransportException(
                TransferTransportErrorCodes.InvalidHandshakeToken,
                "Peer HandshakeFrame token or session ID mismatch on WebRTC DataChannel.");
        }

        channel.MarkAuthenticated(remoteFingerprint);
        return channel;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _localDescriptionTcs.TrySetCanceled();
        _channelAvailableTcs.TrySetCanceled();

        _disposeCts.Cancel();
        _candidateQueue.Writer.TryComplete();

        try
        {
            await _candidatePumpTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }

        _disposeCts.Dispose();

        try
        {
            _peerConnection.OnLocalDescriptionSafe -= _onLocalDescriptionHandler;
            _peerConnection.OnCandidateSafe -= _onCandidateHandler;
            _peerConnection.OnDataChannel -= _onDataChannelHandler;
            _peerConnection.OnConnectionStateChange -= _onConnectionStateChangeHandler;
        }
        catch
        {
        }

        WebRtcDataChannelAdapter? channelToDispose;
        lock (_channelSync)
        {
            channelToDispose = _dataChannel;
            _dataChannel = null;
        }

        if (channelToDispose != null)
        {
            await channelToDispose.DisposeAsync().ConfigureAwait(false);
        }

        // Offload native peer connection disposal off callback threads.
        await Task.Run(() =>
        {
            try
            {
                _peerConnection.Dispose();
            }
            catch
            {
            }
        }).ConfigureAwait(false);

        _onDisposed();
    }

    private void AttachDataChannel(IRtcDataChannel nativeChannel)
    {
        var adapter = new WebRtcDataChannelAdapter(
            nativeChannel,
            StreamIndex.FromInt(0),
            _options.SendBufferHighWatermarkBytes,
            _options.SendBufferLowWatermarkBytes);

        lock (_channelSync)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                _ = Task.Run(() => adapter.DisposeAsync().AsTask());
                return;
            }

            _dataChannel = adapter;
        }

        _channelAvailableTcs.TrySetResult(adapter);
    }

    private void OnLocalDescription(IRtcPeerConnection _, RtcDescription description)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        string sdp = description.Sdp ?? string.Empty;
        var validation = SdpSecurityValidator.ValidateAndExtractFingerprint(sdp, expectedRemoteFingerprint: null);
        if (validation.IsFailure)
        {
            _localDescriptionTcs.TrySetException(
                new TransferTransportException(validation.Error.Code, validation.Error.Description));
            return;
        }

        LocalDtlsFingerprint = validation.Value;
        _localDescriptionTcs.TrySetResult(sdp);
    }

    private void OnLocalCandidate(IRtcPeerConnection _, RtcCandidate candidate)
    {
        if (Volatile.Read(ref _disposed) != 0 || string.IsNullOrWhiteSpace(candidate.Content))
        {
            return;
        }

        _candidateQueue.Writer.TryWrite(new TransferIceCandidateDiscoveredEvent(
            Guid.NewGuid(),
            _clock.UtcNow,
            SessionId,
            RemotePeerId,
            candidate.Content,
            candidate.Mid,
            LocalDtlsFingerprint));
    }

    private void OnRemoteDataChannel(IRtcPeerConnection sender, IRtcDataChannel channel)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    channel.Dispose();
                }
                catch
                {
                }
            });
            return;
        }

        AttachDataChannel(channel);
    }

    private void OnConnectionStateChange(IRtcPeerConnection _, rtcState state)
    {
        if (state == rtcState.RTC_FAILED)
        {
            _channelAvailableTcs.TrySetException(
                new TransferTransportException(
                    TransferTransportErrorCodes.DirectNatTraversalFailed,
                    "WebRTC ICE/DTLS peer connection entered FAILED state."));
        }
        else if (state == rtcState.RTC_CLOSED && Volatile.Read(ref _disposed) == 0)
        {
            _channelAvailableTcs.TrySetException(
                new TransferTransportException(
                    TransferTransportErrorCodes.DirectNatTraversalFailed,
                    "WebRTC ICE/DTLS peer connection closed before DataChannel opened."));
        }
    }

    private async Task PumpOutboundCandidatesAsync()
    {
        try
        {
            await foreach (TransferIceCandidateDiscoveredEvent evt in _candidateQueue.Reader.ReadAllAsync(_disposeCts.Token).ConfigureAwait(false))
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    break;
                }

                try
                {
                    await _eventPublisher.PublishAsync(evt, _disposeCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Subscriber exceptions must never crash the candidate dispatch pump.
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown via _disposeCts.
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(WebRtcPeerConnectionAdapter));
        }
    }

    private static void EnsureNativeRuntimePreloaded()
    {
        if (Interlocked.Exchange(ref _nativePreloaded, 1) == 0)
        {
            RtcTools.Preload();
        }
    }
}
