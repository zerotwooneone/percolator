using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Security.Model;

namespace Percolator.Domain.Security.Ports;

/// <summary>
/// Port for persisting and retrieving client-side group cryptographic credentials.
/// </summary>
public interface IGroupCredentialsRepository
{
    Task<GroupCredentials?> GetAsync(ChannelId channelId, CancellationToken cancellationToken = default);
    Task SaveAsync(GroupCredentials credentials, CancellationToken cancellationToken = default);
    Task DeleteAsync(ChannelId channelId, CancellationToken cancellationToken = default);
}
