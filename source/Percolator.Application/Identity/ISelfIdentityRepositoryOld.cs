using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Percolator.Application.Identity
{
    public interface ISelfIdentityRepositoryOld
    {
        Task<SelfIdentityDto?> GetByIdAsync(int id);
        Task<SelfIdentityDto?> GetByNameWithFallbackAsync(string name, string fallbackName);
        Task<IReadOnlyList<SelfIdentityDto>> ListAsync();
        Task<int> CreateAsync(Guid peerId, string name);
    }
}
