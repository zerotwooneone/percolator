namespace Percolator.Infrastructure.Plugins.FileTransfer.WebRtc;

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
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
/// Sub-frames oversized payloads (> 64 KB) into 64 KB SCTP fragments to eliminate WebRTC message size cliffs.
/// </summary>
internal sealed class WebRtcDataChannelAdapter : ITransferDataChannel
{
    internal const int MaxSctpMessageBytes = 65_536;
    private const byte FragmentOpcode = 0xFF;
    private const int FragmentHeaderBytes = 13; // [0xFF (1B) | uint32 msgId (4B) | uint32 totalLen (4B) | uint32 offset (4B)]
    private const int MaxFragmentPayloadBytes = MaxSctpMessageBytes - FragmentHeaderBytes; // 65_523
    private const int MaxConcurrentReassemblies = 4;
    private const long MaxBufferedInboundBytes = 8L * 1024 * 1024; // 8 MB cap per channel
    private const int InboundChannelCapacity = 128;

    private readonly IRtcDataChannel _channel;
    private readonly Channel<PooledWireFrame> _inboundFrames;
    private readonly TaskCompletionSource<bool> _openTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Action? _onDisposed;

    // Strong references to event delegates so GC cannot collect callback targets while native handles live.
    private readonly Action<IRtcDataChannel> _onOpenHandler;
    private readonly Action<IRtcDataChannel> _onCloseHandler;
    private readonly Action<IRtcDataChannel, string> _onErrorHandler;
    private readonly Action<IRtcDataChannel, RtcBinaryReceivedEventSafe> _onBinaryHandler;

    private readonly Dictionary<uint, InboundReassembly> _reassemblies = new();
    private readonly object _reassemblyLock = new();
    private int _nextFragmentMsgId;
    private long _bufferedInboundBytes;

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

        _inboundFrames = Channel.CreateBounded<PooledWireFrame>(new BoundedChannelOptions(InboundChannelCapacity)
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

            if (encoded.Length <= MaxSctpMessageBytes)
            {
                _channel.Send(encoded.Span);
            }
            else
            {
                uint msgId = unchecked((uint)Interlocked.Increment(ref _nextFragmentMsgId));
                byte[] scratch = ArrayPool<byte>.Shared.Rent(MaxSctpMessageBytes);
                try
                {
                    int offset = 0;
                    while (offset < encoded.Length)
                    {
                        int sliceLen = Math.Min(MaxFragmentPayloadBytes, encoded.Length - offset);
                        scratch[0] = FragmentOpcode;
                        BinaryPrimitives.WriteUInt32BigEndian(scratch.AsSpan(1, 4), msgId);
                        BinaryPrimitives.WriteUInt32BigEndian(scratch.AsSpan(5, 4), (uint)encoded.Length);
                        BinaryPrimitives.WriteUInt32BigEndian(scratch.AsSpan(9, 4), (uint)offset);
                        encoded.Span.Slice(offset, sliceLen).CopyTo(scratch.AsSpan(FragmentHeaderBytes, sliceLen));

                        _channel.Send(scratch.AsSpan(0, FragmentHeaderBytes + sliceLen));
                        offset += sliceLen;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(scratch, clearArray: false);
                }
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async ValueTask<IStreamFramePayload> ReceiveFrameAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();

        PooledWireFrame rawFrame;
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
            using (rawFrame)
            {
                return SwarmFrameCodec.DecodeFramed(rawFrame.Span);
            }
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

        // Drain any pending queued PooledWireFrames
        while (_inboundFrames.Reader.TryRead(out PooledWireFrame? frame))
        {
            frame.Dispose();
        }

        // Drain any in-flight reassembly buffers
        lock (_reassemblyLock)
        {
            foreach (InboundReassembly reassembly in _reassemblies.Values)
            {
                Interlocked.Add(ref _bufferedInboundBytes, -reassembly.TotalLength);
                ArrayPool<byte>.Shared.Return(reassembly.Buffer, clearArray: false);
            }
            _reassemblies.Clear();
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
        if (data.Length == 0)
        {
            return;
        }

        if (data[0] == FragmentOpcode)
        {
            if (data.Length <= FragmentHeaderBytes)
            {
                FailChannelOffThread(new InvalidDataException(
                    $"Inbound WebRTC fragment length {data.Length} is too short to contain header."));
                return;
            }

            uint msgId = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(1, 4));
            uint totalLen = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(5, 4));
            uint offset = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(9, 4));
            int payloadLen = data.Length - FragmentHeaderBytes;

            if (totalLen < SwarmFrameCodec.FrameHeaderSizeBytes || totalLen > SwarmFrameCodec.MaxWireFrameSizeBytes)
            {
                FailChannelOffThread(new InvalidDataException(
                    $"WebRTC fragment declared total length {totalLen} is out of bounds [{SwarmFrameCodec.FrameHeaderSizeBytes}..{SwarmFrameCodec.MaxWireFrameSizeBytes}]."));
                return;
            }

            if (offset + (uint)payloadLen > totalLen)
            {
                FailChannelOffThread(new InvalidDataException(
                    $"WebRTC fragment offset {offset} + payload {payloadLen} exceeds total length {totalLen}."));
                return;
            }

            PooledWireFrame? completedFrame = null;
            lock (_reassemblyLock)
            {
                if (!_reassemblies.TryGetValue(msgId, out InboundReassembly? reassembly))
                {
                    if (_reassemblies.Count >= MaxConcurrentReassemblies)
                    {
                        FailChannelOffThread(new IOException("Exceeded maximum concurrent frame reassemblies."));
                        return;
                    }

                    long currentBuffered = Interlocked.Add(ref _bufferedInboundBytes, (int)totalLen);
                    if (currentBuffered > MaxBufferedInboundBytes)
                    {
                        Interlocked.Add(ref _bufferedInboundBytes, -(int)totalLen);
                        FailChannelOffThread(new IOException("Inbound WebRTC DataChannel memory budget exceeded."));
                        return;
                    }

                    byte[] rented = ArrayPool<byte>.Shared.Rent((int)totalLen);
                    reassembly = new InboundReassembly(msgId, (int)totalLen, rented);
                    _reassemblies[msgId] = reassembly;
                }
                else
                {
                    if (reassembly.TotalLength != (int)totalLen)
                    {
                        FailChannelOffThread(new InvalidDataException(
                            $"WebRTC fragment total length mismatch for msgId {msgId}: expected {reassembly.TotalLength}, got {totalLen}."));
                        return;
                    }
                }

                if (offset != (uint)reassembly.ReceivedBytes)
                {
                    FailChannelOffThread(new InvalidDataException(
                        $"WebRTC fragment offset mismatch for msgId {msgId}: expected sequential offset {reassembly.ReceivedBytes}, got {offset}."));
                    return;
                }

                data.Slice(FragmentHeaderBytes, payloadLen).CopyTo(reassembly.Buffer.AsSpan((int)offset, payloadLen));
                reassembly.ReceivedBytes += payloadLen;

                if (reassembly.ReceivedBytes == reassembly.TotalLength)
                {
                    _reassemblies.Remove(msgId);
                    completedFrame = new PooledWireFrame(this, reassembly.Buffer, reassembly.TotalLength);
                }
            }

            if (completedFrame != null)
            {
                if (!_inboundFrames.Writer.TryWrite(completedFrame))
                {
                    completedFrame.Dispose();
                    FailChannelOffThread(new IOException("Inbound WebRTC DataChannel frame queue overflowed."));
                }
            }
        }
        else
        {
            if (data.Length < SwarmFrameCodec.FrameHeaderSizeBytes ||
                data.Length > SwarmFrameCodec.MaxWireFrameSizeBytes)
            {
                FailChannelOffThread(new InvalidDataException(
                    $"Inbound WebRTC frame length {data.Length} violates bounds [{SwarmFrameCodec.FrameHeaderSizeBytes}..{SwarmFrameCodec.MaxWireFrameSizeBytes}]."));
                return;
            }

            long currentBuffered = Interlocked.Add(ref _bufferedInboundBytes, data.Length);
            if (currentBuffered > MaxBufferedInboundBytes)
            {
                Interlocked.Add(ref _bufferedInboundBytes, -data.Length);
                FailChannelOffThread(new IOException("Inbound WebRTC DataChannel memory budget exceeded."));
                return;
            }

            byte[] rented = ArrayPool<byte>.Shared.Rent(data.Length);
            data.CopyTo(rented);
            var frame = new PooledWireFrame(this, rented, data.Length);

            if (!_inboundFrames.Writer.TryWrite(frame))
            {
                frame.Dispose();
                FailChannelOffThread(new IOException("Inbound WebRTC DataChannel frame queue overflowed."));
            }
        }
    }

    private void FailChannelOffThread(Exception ex)
    {
        _inboundFrames.Writer.TryComplete(ex);
        _ = Task.Run(() => DisposeAsync().AsTask());
    }

    internal void ReleaseBufferedWireFrame(byte[] buffer, int length)
    {
        Interlocked.Add(ref _bufferedInboundBytes, -length);
        ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(WebRtcDataChannelAdapter));
        }
    }

    private sealed class InboundReassembly
    {
        public uint MsgId { get; }
        public int TotalLength { get; }
        public byte[] Buffer { get; }
        public int ReceivedBytes { get; set; }

        public InboundReassembly(uint msgId, int totalLength, byte[] buffer)
        {
            MsgId = msgId;
            TotalLength = totalLength;
            Buffer = buffer;
        }
    }

    internal sealed class PooledWireFrame : IDisposable
    {
        private readonly WebRtcDataChannelAdapter _owner;
        private byte[]? _rentedBuffer;

        public int Length { get; }
        public ReadOnlySpan<byte> Span => _rentedBuffer != null ? _rentedBuffer.AsSpan(0, Length) : ReadOnlySpan<byte>.Empty;

        public PooledWireFrame(WebRtcDataChannelAdapter owner, byte[] rentedBuffer, int length)
        {
            _owner = owner;
            _rentedBuffer = rentedBuffer;
            Length = length;
        }

        public void Dispose()
        {
            byte[]? buf = Interlocked.Exchange(ref _rentedBuffer, null);
            if (buf != null)
            {
                _owner.ReleaseBufferedWireFrame(buf, Length);
            }
        }
    }
}
