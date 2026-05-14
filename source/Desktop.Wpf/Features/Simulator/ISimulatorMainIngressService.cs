using Percolator.Contracts;

namespace Desktop.Wpf.Features.Simulator;

public interface ISimulatorToMainTransportService
{
    Task SendEstablishDirectSessionToMainAsync(EstablishDirectSessionRequest request, CancellationToken cancellationToken = default);

    Task<EstablishSessionResponse> SendEstablishSessionToMainAsync(EstablishSessionRequest request, CancellationToken cancellationToken = default);

    Task<DeliverOpaqueMessageResponse> SendOpaqueMessageToMainAsync(DeliverOpaqueMessageRequest request, CancellationToken cancellationToken = default);

    Task<DeliverInviteHandshakeResponseAck> DeliverInviteHandshakeResponseToMainAsync(InviteHandshakeResponse response, CancellationToken cancellationToken = default);
}
