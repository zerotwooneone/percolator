using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Windows;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Percolator.Application.Network;
using Percolator.Contracts;

namespace Desktop.Wpf.Features.Simulator;

public sealed class SimulatorOutboundInterceptor : ISimulatorOutboundInterceptor
{
    private readonly ISimulatorStateService _state;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SimulatorOutboundInterceptor> _logger;

    public SimulatorOutboundInterceptor(
        ISimulatorStateService state,
        IServiceScopeFactory scopeFactory,
        ILogger<SimulatorOutboundInterceptor> logger)
    {
        _state = state;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public bool TryEstablishDirectSession(
        DnsEndPoint endpoint,
        EstablishDirectSessionRequest request,
        CancellationToken cancellationToken,
        out Task<EstablishDirectSessionResponse> result)
    {
        if (!TryResolveSimulatedPeerId(endpoint, out var peerId))
        {
            result = Task.FromResult(new EstablishDirectSessionResponse
            {
                Version = 1,
                Never = new EstablishDirectSessionResponse.Types.Never()
            });
            return false;
        }

        _logger.LogInformation("[simulator] Intercepted EstablishDirectSession to {SimPeer}", peerId);
        result = EstablishDirectSessionAsync(endpoint, peerId, request, cancellationToken);
        return true;
    }

    public bool TryDeliverInviteHandshakeResponse(
        DnsEndPoint endpoint,
        InviteHandshakeResponse request,
        out Task<DeliverInviteHandshakeResponseAck> result)
    {
        if (!TryResolveSimulatedPeerId(endpoint, out var peerId))
        {
            result = Task.FromResult(new DeliverInviteHandshakeResponseAck { Version = 1 });
            return false;
        }

        result = DeliverInviteHandshakeResponseAsync(peerId, request);
        return true;
    }

    public bool TryEstablishSession(
        DnsEndPoint endpoint,
        EstablishSessionRequest request,
        CancellationToken cancellationToken,
        out Task<EstablishSessionResponse> result)
    {
        if (!TryResolveSimulatedPeerId(endpoint, out var peerId))
        {
            result = Task.FromResult(new EstablishSessionResponse
            {
                Version = 1,
                Never = new EstablishSessionResponse.Types.Never { Version = 1 }
            });
            return false;
        }

        _logger.LogInformation("[simulator] Intercepted EstablishSession to {SimPeer}", peerId);
        result = InvokeInScopeAsync(
            peerRuntime => peerRuntime.ReceiveEstablishSessionFromMainAsync(peerId, request, cancellationToken));
        return true;
    }

    public bool TryDeliverOpaqueMessage(
        DnsEndPoint endpoint,
        DeliverOpaqueMessageRequest request,
        CancellationToken cancellationToken,
        out Task<DeliverOpaqueMessageResponse> result)
    {
        if (!TryResolveSimulatedPeerId(endpoint, out var peerId))
        {
            result = Task.FromResult(new DeliverOpaqueMessageResponse { Version = 1 });
            return false;
        }

        result = InvokeInScopeAsync(
            peerRuntime => peerRuntime.ReceiveOpaqueMessageFromMainAsync(peerId, request, cancellationToken));
        return true;
    }

    private async Task<DeliverInviteHandshakeResponseAck> DeliverInviteHandshakeResponseAsync(Guid simulatedPeerId, InviteHandshakeResponse response)
    {
        _logger.LogInformation("[simulator] Intercepted DeliverInviteHandshakeResponse to {SimPeer}", simulatedPeerId);
        await InvokeInScopeAsync(
                peerRuntime => peerRuntime.ReceiveInviteHandshakeResponseFromMainAsync(simulatedPeerId, response))
            .ConfigureAwait(false);
        return new DeliverInviteHandshakeResponseAck { Version = 1 };
    }

    private async Task<EstablishDirectSessionResponse> EstablishDirectSessionAsync(
        DnsEndPoint endpoint,
        Guid simulatedPeerId,
        EstablishDirectSessionRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var peerRuntime = scope.ServiceProvider.GetRequiredService<ISimulatedPeerRuntimeService>();
            var messageService = scope.ServiceProvider.GetRequiredService<PercolatorMessageService>();
            var directory = scope.ServiceProvider.GetRequiredService<ISimulatedPeerDirectory>();

            var acceptance = await peerRuntime.AcceptReverseSignalInviteAsync(
                    simulatedPeerId: simulatedPeerId,
                    inviterPeerId: Guid.Empty,
                    invite: request,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var ctx = new ServerCallContextStub(
                method: "/percolator.contracts.TransportService/DeliverInviteHandshakeResponse",
                peer: $"ipv4:{endpoint.Host}:{endpoint.Port}",
                deadline: DateTime.UtcNow.AddMinutes(1),
                requestHeaders: new Metadata(),
                cancellationToken: cancellationToken);

            await messageService.DeliverInviteHandshakeResponse(acceptance.Response, ctx).ConfigureAwait(false);

            // Chunk H.1: reflect the successful session creation in the simulator UI state.
            // This is the Main -> Simulator initiation path; without this, the Handshakes tab
            // can remain stale even though the simulated peer has created the ratchet session.
            var model = directory.Peers.FirstOrDefault(p => p.PeerId == simulatedPeerId);
            if (model is not null)
            {
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher is not null)
                {
                    await dispatcher.InvokeAsync(model.MarkEstablished);
                }
                else
                {
                    model.MarkEstablished();
                }
            }

            return new EstablishDirectSessionResponse
            {
                Version = 1,
                Queued = new EstablishDirectSessionResponse.Types.Queued
                {
                    Version = 1,
                    RequestCorrelationId = acceptance.Response.RequestCorrelationId
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[simulator] Failed to intercept EstablishDirectSession to {SimPeer}", simulatedPeerId);
            return new EstablishDirectSessionResponse
            {
                Version = 1,
                Never = new EstablishDirectSessionResponse.Types.Never()
            };
        }
    }

    private async Task InvokeInScopeAsync(Func<ISimulatedPeerRuntimeService, Task> work)
    {
        if (work is null) throw new ArgumentNullException(nameof(work));

        using var scope = _scopeFactory.CreateScope();
        var peerRuntime = scope.ServiceProvider.GetRequiredService<ISimulatedPeerRuntimeService>();
        await work(peerRuntime).ConfigureAwait(false);
    }

    private async Task<T> InvokeInScopeAsync<T>(Func<ISimulatedPeerRuntimeService, Task<T>> work)
    {
        if (work is null) throw new ArgumentNullException(nameof(work));

        using var scope = _scopeFactory.CreateScope();
        var peerRuntime = scope.ServiceProvider.GetRequiredService<ISimulatedPeerRuntimeService>();
        return await work(peerRuntime).ConfigureAwait(false);
    }

    private bool TryResolveSimulatedPeerId(DnsEndPoint endpoint, out Guid simulatedPeerId)
    {
        simulatedPeerId = default;

        if (!IPAddress.TryParse(endpoint.Host, out var ip))
        {
            return false;
        }

        var bytes = ip.GetAddressBytes();
        if (bytes.Length != 4)
        {
            return false;
        }

        if (bytes[0] != 127 || bytes[1] != 77)
        {
            return false;
        }

        var match = _state.Peers.FirstOrDefault(p =>
            string.Equals(p.Connection.Host, endpoint.Host, StringComparison.OrdinalIgnoreCase)
            && p.Connection.Port == endpoint.Port);

        if (match is null)
        {
            return false;
        }

        simulatedPeerId = match.PeerId;
        return true;
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
        protected override string HostCore => "";
        protected override string PeerCore => _peer;
        protected override DateTime DeadlineCore => _deadline;
        protected override Metadata RequestHeadersCore => _requestHeaders;
        protected override CancellationToken CancellationTokenCore => _cancellationToken;
        protected override Metadata ResponseTrailersCore => new();
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore => new("", new Dictionary<string, List<AuthProperty>>());

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) => throw new NotSupportedException();
        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
    }
}
