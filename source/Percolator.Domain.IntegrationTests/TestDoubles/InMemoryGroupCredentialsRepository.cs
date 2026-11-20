using System.Collections.Concurrent;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Security.Model;
using Percolator.Domain.Security.Ports;

namespace Percolator.Domain.IntegrationTests.TestDoubles;

public sealed class InMemoryGroupCredentialsRepository : IGroupCredentialsRepository
{
    private readonly ConcurrentDictionary<ChannelId, GroupCredentials> _storage = new();

    public Task<GroupCredentials?> GetAsync(ChannelId channelId, CancellationToken cancellationToken = default)
    {
        _storage.TryGetValue(channelId, out var creds);
        return Task.FromResult(creds);
    }

    public Task SaveAsync(GroupCredentials credentials, CancellationToken cancellationToken = default)
    {
        _storage[credentials.Id] = credentials;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(ChannelId channelId, CancellationToken cancellationToken = default)
    {
        _storage.TryRemove(channelId, out _);
        return Task.CompletedTask;
    }
}
