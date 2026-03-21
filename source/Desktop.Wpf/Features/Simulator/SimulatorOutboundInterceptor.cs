using System;
using System.Linq;
using System.Net;
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

            var acceptance = await peerRuntime.AcceptReverseSignalInviteAsync(
                    simulatedPeerId: simulatedPeerId,
                    inviterPeerId: Guid.Empty,
                    invite: request,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            // Chunk H.2: do NOT auto-deliver the response to Main. Queue it so the simulator UI
            // can present an explicit Accept button to trigger delivery.
            if (!Guid.TryParse(acceptance.Response.RequestCorrelationId, out var corr))
            {
                corr = Guid.NewGuid();
            }

            await peerRuntime.QueueInviteHandshakeResponseForDeliveryToMainAsync(
                    simulatedPeerId: simulatedPeerId,
                    requestCorrelationId: corr,
                    response: acceptance.Response,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return new EstablishDirectSessionResponse
            {
                Version = 1,
                Queued = new EstablishDirectSessionResponse.Types.Queued
                {
                    Version = 1,
                    RequestCorrelationId = corr.ToString()
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

        var match = _state.SnapshotPeers().FirstOrDefault(p =>
            !string.IsNullOrWhiteSpace(p.Host)
            && string.Equals(p.Host, endpoint.Host, StringComparison.OrdinalIgnoreCase)
            && p.Port == endpoint.Port);

        if (match is null)
        {
            return false;
        }

        simulatedPeerId = match.PeerId;
        return true;
    }

}
