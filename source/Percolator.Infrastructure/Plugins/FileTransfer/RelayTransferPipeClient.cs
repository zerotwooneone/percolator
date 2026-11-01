namespace Percolator.Infrastructure.Plugins.FileTransfer;

using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Apps.FileTransfer.Ports;

public sealed class RelayTransferPipeClient : IAsyncDisposable
{
    private readonly Func<RelayRendezvousDescriptor, StreamIndex, CancellationToken, ValueTask<Stream>>? _streamFactory;
    private int _disposed;

    public RelayTransferPipeClient(Func<RelayRendezvousDescriptor, StreamIndex, CancellationToken, ValueTask<Stream>>? streamFactory = null)
    {
        _streamFactory = streamFactory;
    }

    public async ValueTask<ITransferStream> OpenPipeStreamAsync(
        RelayRendezvousDescriptor rendezvous,
        StreamIndex streamIndex,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rendezvous);
        ThrowIfDisposed();

        if (_streamFactory != null)
        {
            Stream customStream = await _streamFactory(rendezvous, streamIndex, ct).ConfigureAwait(false);
            return new TcpTransferStream(streamIndex, customStream);
        }

        var uri = new Uri(rendezvous.EndpointUri.Value);
        int port = uri.Port > 0 ? uri.Port : (uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? 443 : 80);

        Socket socket = new(SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true
        };

        using CancellationTokenSource connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectTimeout.CancelAfter(TimeSpan.FromSeconds(5));

        await socket.ConnectAsync(uri.Host, port, connectTimeout.Token).ConfigureAwait(false);
        Stream stream = new NetworkStream(socket, ownsSocket: true);

        string requestHeader = $"POST {uri.AbsolutePath.TrimEnd('/')}/stream/{streamIndex.Value} HTTP/1.1\r\n" +
                               $"Host: {uri.Host}\r\n" +
                               $"X-Pipe-Auth: {Convert.ToHexString(rendezvous.Token.Span)}\r\n" +
                               $"Connection: Upgrade\r\n" +
                               $"Upgrade: percolator-relay-pipe\r\n\r\n";

        byte[] headerBytes = Encoding.UTF8.GetBytes(requestHeader);
        await stream.WriteAsync(headerBytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        return new TcpTransferStream(streamIndex, stream, socket);
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(RelayTransferPipeClient));
        }
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        return ValueTask.CompletedTask;
    }
}
