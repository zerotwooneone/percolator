using Percolator.Application.Ingress;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Model;
using Percolator.PluginSdk;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Application.Ports;

public interface IHandshakeService
{
    ValueTask<DomainResult<DirectRatchetSession>> InitiateHandshakeAsync(
        PublicIdentityId ownerId,
        DeviceId ownerDeviceId,
        PublicIdentityId remotePeerId,
        PreKeyBundle remoteBundle,
        AppId? initialAppId = null,
        ReadOnlyMemory<byte>? initialPayload = null,
        CancellationToken ct = default);

    ValueTask<DomainResult> ReceiveInvitationAsync(
        InboundHandshakeEnvelope envelope,
        CancellationToken ct = default);

    ValueTask<DomainResult> CompletePendingHandshakeAsync(
        PublicIdentityId recipientId,
        PublicIdentityId senderId,
        CancellationToken ct = default);
}
