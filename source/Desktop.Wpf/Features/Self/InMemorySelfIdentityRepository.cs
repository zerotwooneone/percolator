using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Percolator.Application.Identity;

namespace Desktop.Wpf.Features.Self;

public sealed class InMemorySelfIdentityRepository : ISelfIdentityRepository
{
    private static readonly SelfIdentityDto Dummy = new()
    {
        Id = 1,
        PeerId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeffffffff"),
        Name = "Operator"
    };

    public Task<SelfIdentityDto?> GetByIdAsync(int id)
        => Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_=>Task.FromResult<SelfIdentityDto?>(id == 1 ? Dummy : null)).Unwrap();

    public Task<SelfIdentityDto?> GetByNameWithFallbackAsync(string name, string fallbackName)
        => Task.FromResult<SelfIdentityDto?>(Dummy);
    public Task<IReadOnlyList<SelfIdentityDto>> ListAsync()
        => Task.FromResult<IReadOnlyList<SelfIdentityDto>>(new[] { Dummy });

    public Task<int> CreateAsync(Guid peerId, string name)
        => Task.FromResult(Dummy.Id);
}
