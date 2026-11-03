namespace Percolator.Infrastructure.Plugins.FileTransfer;

using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.MultiStreamEngine;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Apps.FileTransfer.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Transport;

public sealed class TcpTransferStream : ITransferStream
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
        ThrowIfDisposed();

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
            try
            {
                _sendLock.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    public async ValueTask<IStreamFramePayload> ReceiveFrameAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();

        byte[] header = new byte[5];
        await _receiveLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ReadExactAsync(_stream, header, ct).ConfigureAwait(false);

            byte opcode = header[0];
            uint payloadLength = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(1, 4));

            if (payloadLength > SwarmFrameCodec.MaxPayloadSizeBytes)
            {
                await DisposeAsync().ConfigureAwait(false);
                throw new InvalidDataException($"Frame payload length {payloadLength} exceeds maximum allowed 256 KB limit.");
            }

            byte[] payload = new byte[payloadLength];
            if (payloadLength > 0)
            {
                await ReadExactAsync(_stream, payload, ct).ConfigureAwait(false);
            }

            return SwarmFrameCodec.Decode(opcode, payload);
        }
        finally
        {
            try
            {
                _receiveLock.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private static async ValueTask ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.Slice(totalRead), ct).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException($"Stream closed prematurely after reading {totalRead} of {buffer.Length} bytes.");
            }
            totalRead += read;
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(TcpTransferStream));
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
            _socket?.Dispose();
        }
        catch
        {
        }

        try
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            _stream.Dispose();
        }

        _sendLock.Dispose();
        _receiveLock.Dispose();
    }
}

public sealed class TcpMultiStreamPool : ITransferStreamPool
{
    private readonly TransferSessionId _sessionId;
    private readonly PublicIdentityId _peerId;
    private readonly StreamCount _targetStreamCount;
    private readonly CallbackEndpointValidator _validator;
    private readonly RelayTransferPipeClient? _pipeClient;
    private readonly List<ITransferStream> _streams = new();
    private readonly SemaphoreSlim _poolLock = new(1, 1);
    private int _disposed;

    public StreamCount ActiveStreams
    {
        get
        {
            lock (_streams)
            {
                return StreamCount.FromInt(_streams.Count);
            }
        }
    }

    public TcpMultiStreamPool(
        TransferSessionId sessionId,
        PublicIdentityId peerId,
        StreamCount targetStreamCount,
        CallbackEndpointValidator? validator = null,
        RelayTransferPipeClient? pipeClient = null)
    {
        _sessionId = sessionId;
        _peerId = peerId;
        _targetStreamCount = targetStreamCount;
        _validator = validator ?? new CallbackEndpointValidator();
        _pipeClient = pipeClient;
    }

    public async ValueTask ConnectDirectAsync(
        IReadOnlyList<DirectEndpointCandidate> candidates,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ThrowIfDisposed();

        await _poolLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            int needed = _targetStreamCount.Count - _streams.Count;
            if (needed <= 0 || candidates.Count == 0)
            {
                return;
            }

            foreach (DirectEndpointCandidate candidate in candidates)
            {
                if (_streams.Count >= _targetStreamCount.Count)
                {
                    break;
                }

                DomainResult<IPEndPoint> validationResult = await _validator.ValidateEndpointAsync(
                    candidate.Host.Value,
                    candidate.Port.Value,
                    ct: ct).ConfigureAwait(false);

                if (validationResult.IsFailure)
                {
                    continue;
                }

                IPEndPoint endpoint = validationResult.Value;

                try
                {
                    Socket socket = new(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                    {
                        NoDelay = true
                    };

                    using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

                    await socket.ConnectAsync(endpoint, timeoutCts.Token).ConfigureAwait(false);
                    NetworkStream stream = new(socket, ownsSocket: true);

                    StreamIndex streamIndex = StreamIndex.FromInt(_streams.Count);
                    TcpTransferStream transferStream = new(streamIndex, stream, socket);

                    lock (_streams)
                    {
                        _streams.Add(transferStream);
                    }
                }
                catch
                {
                    // Connection attempt failed, try next candidate
                }
            }
        }
        finally
        {
            _poolLock.Release();
        }
    }

    public async ValueTask ConnectRelayPipeAsync(
        RelayRendezvousDescriptor rendezvous,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rendezvous);
        ThrowIfDisposed();

        if (_pipeClient == null)
        {
            throw new InvalidOperationException("RelayTransferPipeClient is required to connect via relay pipe.");
        }

        await _poolLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            int needed = _targetStreamCount.Count - _streams.Count;
            for (int i = 0; i < needed; i++)
            {
                StreamIndex streamIndex = StreamIndex.FromInt(_streams.Count);
                ITransferStream stream = await _pipeClient.OpenPipeStreamAsync(rendezvous, streamIndex, ct).ConfigureAwait(false);
                lock (_streams)
                {
                    _streams.Add(stream);
                }
            }
        }
        finally
        {
            _poolLock.Release();
        }
    }

    public ITransferStream GetStream(StreamIndex index)
    {
        ThrowIfDisposed();

        lock (_streams)
        {
            if (_streams.Count == 0)
            {
                throw new InvalidOperationException("No active streams connected in the pool.");
            }

            int safeIndex = Math.Abs(index.Value) % _streams.Count;
            return _streams[safeIndex];
        }
    }

    public async ValueTask BroadcastFrameAsync(IStreamFramePayload frame, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ThrowIfDisposed();

        List<ITransferStream> snapshot;
        lock (_streams)
        {
            snapshot = new List<ITransferStream>(_streams);
        }

        foreach (ITransferStream stream in snapshot)
        {
            try
            {
                await stream.SendFrameAsync(frame, ct).ConfigureAwait(false);
            }
            catch
            {
                // Fault-tolerant broadcast across streams
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(TcpMultiStreamPool));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        List<ITransferStream> toDispose;
        lock (_streams)
        {
            toDispose = new List<ITransferStream>(_streams);
            _streams.Clear();
        }

        foreach (ITransferStream stream in toDispose)
        {
            try
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        }

        _poolLock.Dispose();
    }
}

public sealed class TcpMultiStreamPoolFactory : ITransferStreamPoolFactory
{
    private readonly CallbackEndpointValidator _validator;
    private readonly RelayTransferPipeClient _pipeClient;

    public TcpMultiStreamPoolFactory(
        RelayTransferPipeClient pipeClient,
        CallbackEndpointValidator? validator = null)
    {
        _pipeClient = pipeClient ?? throw new ArgumentNullException(nameof(pipeClient));
        _validator = validator ?? new CallbackEndpointValidator();
    }

    public ITransferStreamPool CreatePool(TransferSessionId sessionId, PublicIdentityId peerId, StreamCount streamCount)
    {
        return new TcpMultiStreamPool(sessionId, peerId, streamCount, _validator, _pipeClient);
    }

    public ITransferStreamPool CreatePool(TransferSessionId sessionId, PublicIdentityId peerId)
    {
        return CreatePool(sessionId, peerId, StreamCount.Default);
    }
}
