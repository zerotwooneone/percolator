using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Desktop.Common.Services;

public sealed class DesktopIdentityProfileRepository : IIdentityProfileRepository
{
    private readonly ConcurrentDictionary<PublicIdentityId, IdentityProfile> _profiles = new();

    public Task<IdentityProfile?> GetByIdAsync(PublicIdentityId id, CancellationToken cancellationToken = default)
    {
        _profiles.TryGetValue(id, out var profile);
        return Task.FromResult(profile);
    }

    public Task<IReadOnlyList<IdentityProfile>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<IdentityProfile> list = _profiles.Values.ToList();
        return Task.FromResult(list);
    }

    public Task SaveAsync(IdentityProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profiles[profile.Id] = profile;
        return Task.CompletedTask;
    }
}
