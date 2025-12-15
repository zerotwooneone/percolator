using System;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Cryptography;

namespace Percolator.Application.Ingress;

public interface IPendingHandshakeIngress
{
    Task<PendingHandshakeIngressResult> CreateFromInitiatorHelloAsync(
        byte[] initiatorHelloBytes,
        string? displayName = null,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default);
}

public sealed record PendingHandshakeIngressResult(
    PendingHandshakeIngressStatus Status,
    PendingSessionId? PendingSessionId,
    DateTimeOffset? NotUntil,
    string? ErrorMessage);

public enum PendingHandshakeIngressStatus
{
    Accepted = 0,
    RejectedNotReady = 1,
    RejectedInvalid = 2,
    Failed = 3
}
