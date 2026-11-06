using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Simulator.Features.RelayedChat;

public sealed record SimulatorHostedPreKeyReadModel(
    uint KeyId,
    byte[] PublicKey);

public interface ISimulatorRelayQueryService
{
    Task<int> GetHostedOpkCountAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        CancellationToken ct = default);

    Task<bool> HasActivePreKeyAsync(
        PublicIdentityId ownerId,
        CancellationToken ct = default);

    Task<IReadOnlyList<SimulatorHostedPreKeyReadModel>> GetHostedOneTimePreKeysAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        CancellationToken ct = default);
}
