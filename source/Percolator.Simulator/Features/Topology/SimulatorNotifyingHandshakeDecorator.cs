using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Application2.Ingress;
using Percolator.Application2.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Model;
using Percolator.Infrastructure.Persistence;
using Percolator.Infrastructure.Persistence.Repositories;
using Percolator.PluginSdk;
using Percolator.Simulator.Features.Shell;

namespace Percolator.Simulator.Features.Topology;

/// <summary>
/// Ensures that when a peer re-initiates or completes a new X3DH handshake (which generates a new <c>SessionId</c>
/// primary key), any superseded <c>vault_ratchet_sessions</c> row for the same
/// <c>(OwnerIdentityId, OwnerDeviceId, RemotePeerId, RemoteDeviceId)</c> route is removed first so SQLite's
/// <c>ix_vault_ratchet_route</c> unique index is never violated.
/// </summary>
internal sealed class SimulatorRatchetSessionRepositoryDecorator : IRatchetSessionRepository
{
    private readonly SqliteRatchetSessionRepository _inner;
    private readonly PercolatorDbContext _dbContext;

    public SimulatorRatchetSessionRepositoryDecorator(
        SqliteRatchetSessionRepository inner,
        PercolatorDbContext dbContext)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public Task<DirectRatchetSession?> GetSessionAsync(
        PublicIdentityId ownerId,
        PublicIdentityId remotePeerId,
        DeviceId remoteDeviceId,
        CancellationToken ct = default)
        => _inner.GetSessionAsync(ownerId, remotePeerId, remoteDeviceId, ct);

    public async Task SaveSessionAsync(DirectRatchetSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var ownerGuid = session.OwnerIdentityId.Value;
        var ownerDevice = session.OwnerDeviceId.Value;
        var remoteGuid = session.RemotePeerId.Value;
        var remoteDevice = session.RemoteDeviceId.Value;
        var sessionIdStr = session.Id.ToString();

        var supersededSessions = await _dbContext.VaultRatchetSessions
            .Where(s => s.OwnerIdentityId == ownerGuid &&
                        s.OwnerDeviceId == ownerDevice &&
                        s.RemotePeerId == remoteGuid &&
                        s.RemoteDeviceId == remoteDevice &&
                        s.SessionId != sessionIdStr)
            .ToListAsync(ct);

        if (supersededSessions.Count > 0)
        {
            _dbContext.VaultRatchetSessions.RemoveRange(supersededSessions);
            await _dbContext.SaveChangesAsync(ct);
        }

        await _inner.SaveSessionAsync(session, ct);
    }
}

internal sealed class SimulatorNotifyingHandshakeDecorator : IHandshakeService
{
    private readonly IHandshakeService _inner;
    private readonly ISimulatorStateBus _stateBus;
    private readonly SimulatedNodeId _nodeId;

    public SimulatorNotifyingHandshakeDecorator(
        IHandshakeService inner,
        ISimulatorStateBus stateBus,
        SimulatedNodeId nodeId)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _stateBus = stateBus ?? throw new ArgumentNullException(nameof(stateBus));
        _nodeId = nodeId;
    }

    public async ValueTask<DomainResult<DirectRatchetSession>> InitiateHandshakeAsync(
        PublicIdentityId ownerId,
        DeviceId ownerDeviceId,
        PreKeyBundle remoteBundle,
        AppId? initialAppId = null,
        ReadOnlyMemory<byte>? initialPayload = null,
        CancellationToken ct = default)
    {
        var result = await _inner.InitiateHandshakeAsync(
            ownerId,
            ownerDeviceId,
            remoteBundle,
            initialAppId,
            initialPayload,
            ct);

        if (result.IsSuccess)
        {
            _stateBus.Publish(new SimulatorStateSignal(
                _nodeId,
                SignalCategory.RatchetSessionChanged,
                Details: remoteBundle.IdentityId.ToString(),
                Payload: new HandshakeSignalPayload(remoteBundle.IdentityId, "Initiate")));
        }

        return result;
    }

    public async ValueTask<DomainResult> ReceiveInvitationAsync(
        InboundHandshakeEnvelope envelope,
        CancellationToken ct = default)
    {
        var result = await _inner.ReceiveInvitationAsync(envelope, ct);

        if (result.IsSuccess)
        {
            _stateBus.Publish(new SimulatorStateSignal(
                _nodeId,
                SignalCategory.PendingHandshakeChanged,
                Details: envelope.SenderIdentityId.ToString(),
                Payload: new HandshakeSignalPayload(envelope.SenderIdentityId, "ReceiveInvitation")));

            _stateBus.Publish(new SimulatorStateSignal(
                _nodeId,
                SignalCategory.RatchetSessionChanged,
                Details: envelope.SenderIdentityId.ToString(),
                Payload: new HandshakeSignalPayload(envelope.SenderIdentityId, "ReceiveInvitation")));
        }

        return result;
    }

    public async ValueTask<DomainResult> CompletePendingHandshakeAsync(
        PublicIdentityId recipientId,
        PublicIdentityId senderId,
        CancellationToken ct = default)
    {
        var result = await _inner.CompletePendingHandshakeAsync(recipientId, senderId, ct);

        if (result.IsSuccess)
        {
            _stateBus.Publish(new SimulatorStateSignal(
                _nodeId,
                SignalCategory.PendingHandshakeChanged,
                Details: senderId.ToString(),
                Payload: new HandshakeSignalPayload(senderId, "CompletePending")));

            _stateBus.Publish(new SimulatorStateSignal(
                _nodeId,
                SignalCategory.RatchetSessionChanged,
                Details: senderId.ToString(),
                Payload: new HandshakeSignalPayload(senderId, "CompletePending")));
        }

        return result;
    }
}
