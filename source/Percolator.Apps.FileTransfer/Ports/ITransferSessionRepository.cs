using System.Collections.Concurrent;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;

namespace Percolator.Apps.FileTransfer.Ports;

public interface ITransferSessionRepository
{
    private static readonly ConcurrentDictionary<TransferSessionId, TransferSessionTransportContext> FallbackContexts = new();

    ValueTask SaveSessionAsync(TransferSession session, CancellationToken ct = default);
    ValueTask<TransferSession?> GetSessionByIdAsync(TransferSessionId sessionId, CancellationToken ct = default);
    ValueTask<IReadOnlyList<TransferSession>> GetActiveSessionsAsync(CancellationToken ct = default);

    ValueTask SaveTransportContextAsync(TransferSessionTransportContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        FallbackContexts[context.SessionId] = context;
        return ValueTask.CompletedTask;
    }

    ValueTask<TransferSessionTransportContext?> GetTransportContextAsync(TransferSessionId sessionId, CancellationToken ct = default)
    {
        FallbackContexts.TryGetValue(sessionId, out TransferSessionTransportContext? context);
        return ValueTask.FromResult(context);
    }
}
