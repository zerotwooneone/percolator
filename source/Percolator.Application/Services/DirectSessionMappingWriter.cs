using Microsoft.Extensions.Logging;
using Percolator.Identity;
using Percolator.Network;

namespace Percolator.Application.Services;

/// <summary>
/// Implementation of IDirectSessionMappingWriter that persists DirectSession mappings.
/// Uses best-effort semantics: failures are logged but do not propagate as exceptions.
/// </summary>
public sealed class DirectSessionMappingWriter : IDirectSessionMappingWriter
{
    private readonly IDirectSessionRepository _directSessionRepository;
    private readonly ILogger<DirectSessionMappingWriter> _logger;

    public DirectSessionMappingWriter(
        IDirectSessionRepository directSessionRepository,
        ILogger<DirectSessionMappingWriter> logger)
    {
        _directSessionRepository = directSessionRepository;
        _logger = logger;
    }

    public async Task WriteMappingAsync(NetworkPeerId remoteNetworkPeerId, DirectSessionId sessionId, SelfId selfIdentityId, CancellationToken cancellationToken)
    {
        try
        {
            await _directSessionRepository.UpsertAsync(remoteNetworkPeerId, sessionId, new NetworkSelfId(selfIdentityId.Value))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Best-effort: log failure but don't throw
            _logger.LogWarning(ex,
                "Failed to persist DirectSession mapping for RemotePeerId={RemotePeerId}, SessionId={SessionId}, SelfIdentityId={SelfIdentityId}",
                remoteNetworkPeerId, sessionId, selfIdentityId);
        }
    }
}
