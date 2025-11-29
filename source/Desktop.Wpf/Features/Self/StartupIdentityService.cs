using System;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Identity;
using Percolator.Identity.Model;

namespace Desktop.Wpf.Features.Self;

public sealed class StartupIdentityService : IStartupIdentityService
{
    private readonly ISelfIdentityRepository _repo;
    private readonly Func<DateTimeOffset> _clock;

    public StartupIdentityService(ISelfIdentityRepository repo, Func<DateTimeOffset>? clock = null)
    {
        _repo = repo;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<SelfIdentity> ResolveOrCreateAsync(CancellationToken ct = default)
    {
        var existing = await _repo.GetMostRecentAsync(ct).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        var now = _clock();
        var created = new SelfIdentity(new SelfId(0));
        created.TouchLastUsed(now);
        await _repo.SaveAsync(created, ct).ConfigureAwait(false);
        return created;
    }
}
