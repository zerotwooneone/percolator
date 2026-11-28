namespace Percolator.Desktop.Common.Services;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Apps.FileTransfer.Ports;
using Percolator.Infrastructure.Plugins.FileTransfer;

public sealed class DesktopNetworkEndpointDiscoveryPort : INetworkEndpointDiscoveryPort
{
    private readonly ICurrentPersonaService _currentPersonaService;

    public DesktopNetworkEndpointDiscoveryPort(ICurrentPersonaService currentPersonaService)
    {
        _currentPersonaService = currentPersonaService ?? throw new ArgumentNullException(nameof(currentPersonaService));
    }

    public ValueTask<IReadOnlyList<DirectEndpointCandidate>> DiscoverLocalCandidatesAsync(
        DirectWanPolicy wanPolicy,
        CancellationToken ct = default)
    {
        var boundPort = _currentPersonaService.CurrentPersona?.BoundPort;
        var inner = new NetworkInterfaceEndpointDiscoveryPort(boundPort, allowLoopback: true);
        return inner.DiscoverLocalCandidatesAsync(wanPolicy, ct);
    }

    public ValueTask<RelayRendezvousDescriptor?> AllocateRelayRendezvousAsync(
        TransferSessionId sessionId,
        CancellationToken ct = default)
    {
        return ValueTask.FromResult<RelayRendezvousDescriptor?>(null);
    }
}
