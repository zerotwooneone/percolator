namespace Percolator.Infrastructure.Plugins.FileTransfer.WebRtc;

using System;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using DataChannelDotnet;
using DataChannelDotnet.Events;
using DataChannelDotnet.Impl;
using Percolator.Apps.FileTransfer.Features.MultiStreamEngine;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Apps.FileTransfer.Ports;

/// <summary>
/// Quarantined adapter wrapping a DataChannelDotnet IRtcDataChannel behind ITransferDataChannel.
/// Mitigates native callback reentrancy (async off-thread teardown) and delegate GC (strong
/// handler references held until disposal). Backpressure is enforced at the application protocol layer
/// via request-driven chunk windowing (ChunkPipelineScheduler), eliminating unmanaged SCTP buffer polling
/// and reflection into native channel handles.
/// </summary>
internal sealed class WebRtcDataChannelAdapter : ITransferDataChannel
{
    private const int InboundChannelCapacity = 128;

    private readonly IRtcDataChannel _channel;
    private readonly Channel<byte[]> _inboundFrames;
    private readonly TaskCompletionSource<bool> _openTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Action? _onDisposed;

    // Strong references to event delegates so GC cannot collect callback targets while native handles live.
    private readonly Action<IRtcDataChannel> _onOpenHandler;
    private readonly Action<IRtcDataChannel> _onCloseHandler;
    private readonly Action<IRtcDataChannel, string> _onErrorHandler;
    private readonly Action<IRtcDataChannel, RtcBinaryReceivedEventSafe> _onBinaryHandler;

    private CertSha256Fingerprint _verifiedRemoteCertSha256 = CertSha256Fingerprint.FromBytes(new byte[32]);
    private int _disposed;

    public StreamIndex Index { get; }
    public string Label { get; }
    public TransferTransportKind TransportKind => TransferTransportKind.DirectWebRtcDtls;
    public CertSha256Fingerprint VerifiedRemoteCertSha256 => _verifiedRemoteCertSha256;

    public bool IsOpen
    {
        get
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return false;
            }

            try
            {
                return _channel.IsOpen && !_channel.IsClosed;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Transport-level buffered amount. With application-level request windowing (ChunkPipelineScheduler),
    /// outbound frames are throttled by peer requests rather than transport queue polling.
    /// </summary>
    public long BufferedAmount => 0;

    public Task OpenedTask => _openTcs.Task;

    public WebRtcDataChannelAdapter(IRtcDataChannel channel, StreamIndex index)
        : this(channel, index, onDisposed: null)
    {
    }

    public WebRtcDataChannelAdapter(IRtcDataChannel channel, StreamIndex index, Action? onDisposed)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        Index = index;
        Label = channel.Label ?? "percolator-ft-0";
        _onDisposed = onDisposed;

        _inboundFrames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(InboundChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });

        _onOpenHandler = OnChannelOpen;
        _onCloseHandler = OnChannelClose;
        _onErrorHandler = OnChannelError;
        _onBinaryHandler = OnBinaryReceived;

        _channel.OnOpen += _onOpenHandler;
        _channel.OnClose += _onCloseHandler;
        _channel.OnError += _onErrorHandler;
        _channel.OnBinaryReceivedSafe += _onBinaryHandler;

        if (_channel.IsOpen)
        {
            _openTcs.TrySetResult(true);
        }
        else if (_channel.IsClosed)
        {
            _openTcs.TrySetException(new IOException("WebRTC DataChannel was already closed."));
            _inboundFrames.Writer.TryComplete();
        }
    }

    public WebRtcDataChannelAdapter(
        IRtcDataChannel channel,
        StreamIndex index,
        int highWatermarkBytes,
        int lowWatermarkBytes)
        : this(channel, index, onDisposed: null)
    {
    }

    public WebRtcDataChannelAdapter(
        IRtcDataChannel channel,
        StreamIndex index,
        int highWatermarkBytes,
        int lowWatermarkBytes,
        Action? onDisposed)
        : this(channel, index, onDisposed)
    {
    }

    internal void MarkAuthenticated(CertSha256Fingerprint verifiedRemoteCertSha256)
    {
        _verifiedRemoteCertSha256 = verifiedRemoteCertSha256 ?? throw new ArgumentNullException(nameof(verifiedRemoteCertSha256));
    }

    public async ValueTask SendFrameAsync(IStreamFramePayload frame, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ThrowIfDisposed();

        using PooledEncodedFrame encoded = SwarmFrameCodec.EncodePooled(frame);

        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            if (!IsOpen)
            {
                throw new IOException("Cannot send frame on a closed WebRTC DataChannel.");
            }

            _channel.Send(encoded.Span);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async ValueTask<IStreamFramePayload> ReceiveFrameAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();

        byte[] rawFrame;
        try
        {
            rawFrame = await _inboundFrames.Reader.ReadAsync(ct).ConfigureAwait(false);
        }
        catch (ChannelClosedException ex)
        {
            throw new EndOfStreamException("WebRTC DataChannel closed before receiving next frame.", ex);
        }

        try
        {
            return SwarmFrameCodec.DecodeFramed(rawFrame);
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

        _openTcs.TrySetCanceled();
        _inboundFrames.Writer.TryComplete();

        try
        {
            _channel.OnOpen -= _onOpenHandler;
            _channel.OnClose -= _onCloseHandler;
            _channel.OnError -= _onErrorHandler;
            _channel.OnBinaryReceivedSafe -= _onBinaryHandler;
        }
        catch
        {
        }

        // Offload native disposal to a thread-pool thread so teardown never re-enters a libdatachannel callback thread.
        await Task.Run(() =>
        {
            try
            {
                _channel.Dispose();
            }
            catch
            {
            }
        }).ConfigureAwait(false);

        _sendLock.Dispose();
        _onDisposed?.Invoke();
    }

    private void OnChannelOpen(IRtcDataChannel _)
    {
        _openTcs.TrySetResult(true);
    }

    private void OnChannelClose(IRtcDataChannel _)
    {
        _openTcs.TrySetException(new IOException("WebRTC DataChannel closed before opening."));
        _inboundFrames.Writer.TryComplete();
    }

    private void OnChannelError(IRtcDataChannel _, string error)
    {
        var ex = new IOException($"WebRTC DataChannel error: {error}");
        _openTcs.TrySetException(ex);
        _inboundFrames.Writer.TryComplete(ex);
    }

    private void OnBinaryReceived(IRtcDataChannel sender, RtcBinaryReceivedEventSafe evt)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        ReadOnlySpan<byte> data = evt.Data;
        if (data.Length < SwarmFrameCodec.FrameHeaderSizeBytes ||
            data.Length > SwarmFrameCodec.MaxWireFrameSizeBytes)
        {
            var err = new InvalidDataException(
                $"Inbound WebRTC frame length {data.Length} violates bounds [{SwarmFrameCodec.FrameHeaderSizeBytes}..{SwarmFrameCodec.MaxWireFrameSizeBytes}].");
            _inboundFrames.Writer.TryComplete(err);
            _ = Task.Run(() => DisposeAsync().AsTask());
            return;
        }

        byte[] copy = data.ToArray();
        if (!_inboundFrames.Writer.TryWrite(copy))
        {
            // Non-blocking on native callback thread: if consumer stalled past bounded capacity, fail fast off-thread.
            var err = new IOException("Inbound WebRTC DataChannel frame queue overflowed.");
            _inboundFrames.Writer.TryComplete(err);
            _ = Task.Run(() => DisposeAsync().AsTask());
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(WebRtcDataChannelAdapter));
        }
    }
}
