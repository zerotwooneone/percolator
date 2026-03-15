using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Identity.Model;

namespace Desktop.Wpf.Features.Self;

public sealed class StartupIdentityService : IStartupIdentityService
{
    private readonly ISelfIdentityRepository _repo;
    private readonly IClock _clock;

    public StartupIdentityService(ISelfIdentityRepository repo, IClock clock)
    {
        _repo = repo;
        _clock = clock;
    }

    public async Task<SelfIdentity> ResolveOrCreateAsync(CancellationToken ct = default)
    {
        var existing = await _repo.GetMostRecentAsync(ct).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        var now = _clock.UtcNow;
        var created = new SelfIdentity(new SelfId(0));
        created.TouchLastUsed(now);
        var newId =await _repo.CreateAsync(created, ct).ConfigureAwait(false);
        return await _repo.GetByIdAsync(newId, ct).ConfigureAwait(false)!;
    }
}
