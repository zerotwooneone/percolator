namespace Percolator.Infrastructure.Tests.Transport.Endpoints;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Grpc.Core;

public sealed class TestServerCallContext : ServerCallContext
{
    private readonly Metadata _requestHeaders = new();
    private readonly Metadata _responseTrailers = new();
    private readonly CancellationToken _cancellationToken;

    public TestServerCallContext(CancellationToken cancellationToken = default)
    {
        _cancellationToken = cancellationToken;
    }

    protected override string MethodCore => "TestMethod";
    protected override string HostCore => "localhost";
    protected override string PeerCore => "127.0.0.1";
    protected override DateTime DeadlineCore => DateTime.MaxValue;
    protected override Metadata RequestHeadersCore => _requestHeaders;
    protected override CancellationToken CancellationTokenCore => _cancellationToken;
    protected override Metadata ResponseTrailersCore => _responseTrailers;
    protected override Status StatusCore { get; set; }
    protected override WriteOptions? WriteOptionsCore { get; set; }
    protected override AuthContext AuthContextCore => new(null, new Dictionary<string, List<AuthProperty>>());

    protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) => throw new NotImplementedException();
    protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
}

public sealed class TestServerStreamWriter<T> : IServerStreamWriter<T>
{
    private readonly List<T> _messages = new();
    private readonly object _lock = new();
    private readonly Channel<T> _channel = Channel.CreateUnbounded<T>();

    public IReadOnlyList<T> WrittenMessages
    {
        get
        {
            lock (_lock)
            {
                return _messages.ToArray();
            }
        }
    }

    public async ValueTask<T> ReadNextAsync(CancellationToken cancellationToken = default)
    {
        return await _channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    public WriteOptions? WriteOptions { get; set; }

    public Task WriteAsync(T message)
    {
        lock (_lock)
        {
            _messages.Add(message);
        }
        _channel.Writer.TryWrite(message);
        return Task.CompletedTask;
    }

    public Task WriteAsync(T message, CancellationToken cancellationToken) => WriteAsync(message);
}
