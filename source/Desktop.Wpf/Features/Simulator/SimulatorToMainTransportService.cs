using Percolator.Contracts;

namespace Desktop.Wpf.Features.Simulator;

public sealed class SimulatorToMainTransportService : ISimulatorToMainTransportService
{
    private readonly ISimulatorGrpcClientFactory _clientFactory;

    public SimulatorToMainTransportService(ISimulatorGrpcClientFactory clientFactory)
    {
        _clientFactory = clientFactory;
    }

    public async Task SendEstablishDirectSessionToMainAsync(
        EstablishDirectSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        var client = _clientFactory.CreateClient();
        await client.EstablishDirectSessionAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<EstablishSessionResponse> SendEstablishSessionToMainAsync(
        EstablishSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        var client = _clientFactory.CreateClient();
        return await client.EstablishSessionAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<DeliverOpaqueMessageResponse> SendOpaqueMessageToMainAsync(
        DeliverOpaqueMessageRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        var client = _clientFactory.CreateClient();
        return await client.DeliverOpaqueMessageAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<DeliverInviteHandshakeResponseAck> DeliverInviteHandshakeResponseToMainAsync(
        InviteHandshakeResponse response,
        CancellationToken cancellationToken = default)
    {
        if (response is null) throw new ArgumentNullException(nameof(response));

        var client = _clientFactory.CreateClient();
        return await client.DeliverInviteHandshakeResponseAsync(response, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
