namespace Percolator.Simulator.Features.SwarmSimulation;

using System;
using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Apps.FileTransfer.Features.MultiStreamEngine;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Apps.FileTransfer.Ports;
using Percolator.Infrastructure.Plugins.FileTransfer;

internal sealed class TcpTransferStream : ITransferStream
{
    private readonly Stream _stream;
    private readonly Socket? _socket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _receiveLock = new(1, 1);
    private int _disposed;

    public StreamIndex Index { get; }

    public TcpTransferStream(StreamIndex index, Stream stream, Socket? socket = null)
    {
        Index = index;
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _socket = socket;
    }

    public async ValueTask SendFrameAsync(IStreamFramePayload frame, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (Interlocked.CompareExchange(ref _disposed, 0, 0) == 1)
            throw new ObjectDisposedException(nameof(TcpTransferStream));

        (byte opcode, byte[] payload) = SwarmFrameCodec.Encode(frame);

        byte[] header = new byte[5];
        header[0] = opcode;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(1, 4), (uint)payload.Length);

        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(header, ct).ConfigureAwait(false);
            if (payload.Length > 0)
            {
                await _stream.WriteAsync(payload, ct).ConfigureAwait(false);
            }
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async ValueTask<IStreamFramePayload> ReceiveFrameAsync(CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _disposed, 0, 0) == 1)
            throw new ObjectDisposedException(nameof(TcpTransferStream));

        await _receiveLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            byte[] header = new byte[5];
            await _stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);

            byte opcode = header[0];
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(1, 4));

            byte[] payload = new byte[length];
            if (length > 0)
            {
                await _stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
            }

            return SwarmFrameCodec.Decode(opcode, payload);
        }
        finally
        {
            _receiveLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
            _socket?.Dispose();
            _sendLock.Dispose();
            _receiveLock.Dispose();
        }
    }
}
