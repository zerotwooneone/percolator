using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Simulator.Features.RelayedChat;

public sealed record SimulatorHostedPreKeyReadModel(
    uint KeyId,
    byte[] PublicKey);

public interface ISimulatorRelayQueryService
{
    Task<int> GetHostedOpkCountAsync(
        BlindedRoutingToken routingToken,
        DeviceId deviceId,
        CancellationToken ct = default);

    Task<bool> HasActivePreKeyAsync(
        BlindedRoutingToken routingToken,
        CancellationToken ct = default);

    Task<IReadOnlyList<SimulatorHostedPreKeyReadModel>> GetHostedOneTimePreKeysAsync(
        BlindedRoutingToken routingToken,
        DeviceId deviceId,
        CancellationToken ct = default);
}
