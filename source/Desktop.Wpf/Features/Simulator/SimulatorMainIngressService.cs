using Grpc.Core;
using Percolator.Application.Network;
using Percolator.Contracts;

namespace Desktop.Wpf.Features.Simulator;

public sealed class SimulatorToMainTransportService : ISimulatorToMainTransportService
{
    private readonly PercolatorMessageService _messageService;

    public SimulatorToMainTransportService(PercolatorMessageService messageService)
    {
        _messageService = messageService;
    }

    public Task SendEstablishDirectSessionToMainAsync(
        EstablishDirectSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        var ctx = new ServerCallContextStub(
            method: "/percolator.contracts.TransportService/EstablishDirectSession",
            peer: "ipv4:127.0.0.1:0",
            deadline: DateTime.UtcNow.AddMinutes(1),
            requestHeaders: new Metadata(),
            cancellationToken: cancellationToken);

        _ = request.CalculateSize();
        return _messageService.EstablishDirectSession(request, ctx);
    }

    public Task<EstablishSessionResponse> SendEstablishSessionToMainAsync(
        EstablishSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        var ctx = new ServerCallContextStub(
            method: "/percolator.contracts.TransportService/EstablishSession",
            peer: "ipv4:127.0.0.1:0",
            deadline: DateTime.UtcNow.AddMinutes(1),
            requestHeaders: new Metadata(),
            cancellationToken: cancellationToken);

        _ = request.CalculateSize();
        return _messageService.EstablishSession(request, ctx);
    }

    public Task<DeliverOpaqueMessageResponse> SendOpaqueMessageToMainAsync(
        DeliverOpaqueMessageRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        var ctx = new ServerCallContextStub(
            method: "/percolator.contracts.TransportService/DeliverOpaqueMessage",
            peer: "ipv4:127.0.0.1:0",
            deadline: DateTime.UtcNow.AddMinutes(1),
            requestHeaders: new Metadata(),
            cancellationToken: cancellationToken);

        _ = request.CalculateSize();
        return _messageService.DeliverOpaqueMessage(request, ctx);
    }

    public Task<DeliverInviteHandshakeResponseAck> DeliverInviteHandshakeResponseToMainAsync(
        InviteHandshakeResponse response,
        CancellationToken cancellationToken = default)
    {
        if (response is null) throw new ArgumentNullException(nameof(response));

        var ctx = new ServerCallContextStub(
            method: "/percolator.contracts.TransportService/DeliverInviteHandshakeResponse",
            peer: "ipv4:127.0.0.1:0",
            deadline: DateTime.UtcNow.AddMinutes(1),
            requestHeaders: new Metadata(),
            cancellationToken: cancellationToken);

        return _messageService.DeliverInviteHandshakeResponse(response, ctx);
    }

    private sealed class ServerCallContextStub : ServerCallContext
    {
        private readonly string _method;
        private readonly string _peer;
        private readonly DateTime _deadline;
        private readonly Metadata _requestHeaders;
        private readonly CancellationToken _cancellationToken;

        public ServerCallContextStub(string method, string peer, DateTime deadline, Metadata requestHeaders, CancellationToken cancellationToken)
        {
            _method = method;
            _peer = peer;
            _deadline = deadline;
            _requestHeaders = requestHeaders;
            _cancellationToken = cancellationToken;
        }

        protected override string MethodCore => _method;
        protected override string HostCore => "localhost";
        protected override string PeerCore => _peer;
        protected override DateTime DeadlineCore => _deadline;
        protected override Metadata RequestHeadersCore => _requestHeaders;
        protected override CancellationToken CancellationTokenCore => _cancellationToken;
        protected override Metadata ResponseTrailersCore { get; } = new Metadata();
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore { get; } = new AuthContext(null, new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<AuthProperty>>());

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) => throw new NotImplementedException();
        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
    }
}
