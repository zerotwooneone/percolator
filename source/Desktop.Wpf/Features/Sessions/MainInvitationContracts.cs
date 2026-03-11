using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Desktop.Wpf.Features.Sessions;

public sealed record PendingInvitationDto(
    Guid PendingSessionId,
    Guid RemotePeerId,
    string PeerName,
    Guid RequestCorrelationId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    bool IsRelayed,
    Guid? RelayPeerId,
    string? RelayPeerName,
    string? RelayEndpoint);

public sealed record SentInvitationDto(
    Guid RequestCorrelationId,
    Guid SignedPreKeyId,
    Guid? OneTimePreKeyId,
    Guid? TargetPeerId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc);

public interface IMainInvitationInbox
{
    Task<IReadOnlyList<PendingInvitationDto>> GetOpenAsync(CancellationToken cancellationToken = default);
}

public interface IMainInvitationOutbox
{
    Task<IReadOnlyList<SentInvitationDto>> GetExpiredAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default);
}

public interface IMainInvitationActions
{
    Task<ApproveInvitationResult> ApproveAsync(Guid pendingSessionId, CancellationToken cancellationToken = default);

    Task BurnAsync(Guid pendingSessionId, CancellationToken cancellationToken = default);
}

public abstract record ApproveInvitationResult
{
    public sealed record Accepted(string SendPath, Guid RequestCorrelationId) : ApproveInvitationResult;
    public sealed record RejectedNotReady : ApproveInvitationResult;
    public sealed record RejectedInvalid : ApproveInvitationResult;
    public sealed record RejectedExpired : ApproveInvitationResult;
    public sealed record Failed(string ErrorMessage) : ApproveInvitationResult;
}
