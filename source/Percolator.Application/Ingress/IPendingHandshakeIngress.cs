using System;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Cryptography;

namespace Percolator.Application.Ingress;

public interface IPendingHandshakeIngress
{
    Task<PendingSessionId> CreateFromInitiatorHelloAsync(
        byte[] initiatorHelloBytes,
        string? displayName = null,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default);
}
